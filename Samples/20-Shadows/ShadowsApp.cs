﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Resource = SharpDX.Direct3D12.Resource;
using ShaderResourceViewDimension = SharpDX.Direct3D12.ShaderResourceViewDimension;

namespace DX12GameProgramming
{
    public class ShadowsApp : D3DApp
    {
        private const int ShadowMapSize = 2048;

        private readonly List<FrameResource> _frameResources = new List<FrameResource>(NumFrameResources);
        private readonly List<AutoResetEvent> _fenceEvents = new List<AutoResetEvent>(NumFrameResources);
        private int _currFrameResourceIndex;

        private RootSignature _rootSignature;

        private DescriptorHeap _srvDescriptorHeap;
        private DescriptorHeap[] _descriptorHeaps;

        private readonly Dictionary<string, MeshGeometry> _geometries = new Dictionary<string, MeshGeometry>();
        private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Texture> _textures = new Dictionary<string, Texture>();
        private readonly Dictionary<string, ShaderBytecode> _shaders = new Dictionary<string, ShaderBytecode>();
        private readonly Dictionary<string, PipelineState> _psos = new Dictionary<string, PipelineState>();

        private InputLayoutDescription _inputLayout;

        // List of all the render items.
        private readonly List<RenderItem> _allRitems = new List<RenderItem>();

        // Render items divided by PSO.
        private readonly Dictionary<RenderLayer, List<RenderItem>> _ritemLayers = new Dictionary<RenderLayer, List<RenderItem>>
        {
            [RenderLayer.Opaque] = new List<RenderItem>(),
            [RenderLayer.Debug] = new List<RenderItem>(),
            [RenderLayer.Sky] = new List<RenderItem>()
        };

        private int _skyTexHeapIndex;
        private int _shadowMapHeapIndex;

        private int _nullCubeSrvIndex;

        private GpuDescriptorHandle _nullSrv;

        private PassConstants _mainPassCB = PassConstants.Default;   // Index 0 of pass cbuffer.
        private PassConstants _shadowPassCB = PassConstants.Default; // Index 1 of pass cbuffer.

        private readonly Camera _camera = new Camera();

        private ShadowMap _shadowMap;

        private BoundingSphere _sceneBounds;

        private float _lightNearZ;
        private float _lightFarZ;
        private Vector3 _lightPosW;
        private Matrix _lightView = Matrix.Identity;
        private Matrix _lightProj = Matrix.Identity;
        private Matrix _shadowTransform = Matrix.Identity;

        private float _lightRotationAngle;
        private readonly Vector3[] _baseLightDirections =
        {
            new Vector3(0.57735f, -0.57735f, 0.57735f),
            new Vector3(-0.57735f, -0.57735f, 0.57735f),
            new Vector3(0.0f, -0.707f, -0.707f)
        };
        private readonly Vector3[] _rotatedLightDirections = new Vector3[3];

        private Point _lastMousePos;

        public ShadowsApp()
        {
            MainWindowCaption = "Shadows";

            // Estimate the scene bounding sphere manually since we know how the scene was constructed.
            // The grid is the "widest object" with a width of 20 and depth of 30.0f, and centered at
            // the world space origin.  In general, you need to loop over every world space vertex
            // position and compute the bounding sphere.
            _sceneBounds.Center = Vector3.Zero;
            _sceneBounds.Radius = MathHelper.Sqrtf(10.0f * 10.0f + 15.0f * 15.0f);
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            _camera.Position = new Vector3(0.0f, 2.0f, -15.0f);

            _shadowMap = new ShadowMap(Device, ShadowMapSize, ShadowMapSize);

            LoadTextures();
            BuildRootSignature();
            BuildDescriptorHeaps();
            BuildShadersAndInputLayout();
            BuildShapeGeometry();
            BuildSkullGeometry();
            BuildMaterials();
            BuildRenderItems();
            BuildFrameResources();
            BuildPSOs();

            // Execute the initialization commands.
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // Wait until initialization is complete.
            FlushCommandQueue();
        }

        // Add +6 RTV for cube render target.
        protected override int RtvDescriptorCount => SwapChainBufferCount + 6;
        // Add +1 DSV for cube render target.
        protected override int DsvDescriptorCount => 2;

        protected override void OnResize()
        {
            base.OnResize();

            // The window resized, so update the aspect ratio and recompute the projection matrix.
            _camera.SetLens(MathUtil.PiOverFour, AspectRatio, 1.0f, 1000.0f);
        }

        protected override void Update(GameTimer gt)
        {
            OnKeyboardInput(gt);

            // Cycle through the circular frame resource array.
            _currFrameResourceIndex = (_currFrameResourceIndex + 1) % NumFrameResources;

            // Has the GPU finished processing the commands of the current frame resource?
            // If not, wait until the GPU has completed commands up to this fence point.
            if (CurrFrameResource.Fence != 0 && Fence.CompletedValue < CurrFrameResource.Fence)
            {
                Fence.SetEventOnCompletion(CurrFrameResource.Fence, CurrentFenceEvent.SafeWaitHandle.DangerousGetHandle());
                CurrentFenceEvent.WaitOne();
            }

            //
            // Animate the lights (and hence shadows).
            //

            _lightRotationAngle += 0.1f * gt.DeltaTime;

            Matrix r = Matrix.RotationY(_lightRotationAngle);
            for (int i = 0; i < 3; i++)
                _rotatedLightDirections[i] = Vector3.TransformNormal(_baseLightDirections[i], r);

            UpdateObjectCBs();
            UpdateMaterialBuffer();
            UpdateShadowTransform();
            UpdateMainPassCB(gt);
            UpdateShadowPassCB();
        }

        protected override void Draw(GameTimer gt)
        {
            CommandAllocator cmdListAlloc = CurrFrameResource.CmdListAlloc;

            // Reuse the memory associated with command recording.
            // We can only reset when the associated command lists have finished execution on the GPU.
            cmdListAlloc.Reset();

            // A command list can be reset after it has been added to the command queue via ExecuteCommandList.
            // Reusing the command list reuses memory.
            CommandList.Reset(cmdListAlloc, _psos["opaque"]);

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);

            CommandList.SetGraphicsRootSignature(_rootSignature);

            // Bind all the materials used in this scene. For structured buffers, we can bypass the heap and
            // set as a root descriptor.
            Resource matBuffer = CurrFrameResource.MaterialBuffer.Resource;
            CommandList.SetGraphicsRootShaderResourceView(2, matBuffer.GPUVirtualAddress);

            // Bind null SRV for shadow map pass.
            CommandList.SetGraphicsRootDescriptorTable(3, _nullSrv);

            // Bind all the textures used in this scene. Observe
            // that we only have to specify the first descriptor in the table.
            // The root signature knows how many descriptors are expected in the table.
            CommandList.SetGraphicsRootDescriptorTable(4, _srvDescriptorHeap.GPUDescriptorHandleForHeapStart);

            DrawSceneToShadowMap();

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);
            CommandList.ClearDepthStencilView(DepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.
            CommandList.SetRenderTargets(CurrentBackBufferView, DepthStencilView);

            Resource passCB = CurrFrameResource.PassCB.Resource;
            CommandList.SetGraphicsRootConstantBufferView(1, passCB.GPUVirtualAddress);

            // Bind the sky cube map. For our demos, we just use one "world" cube map representing the environment
            // from far away, so all objects will use the same cube map and we only need to set it once per-frame.
            // If we wanted to use "local" cube maps, we would have to change them per-object, or dynamically
            // index into an array of cube maps.

            GpuDescriptorHandle skyTexDescriptor = _srvDescriptorHeap.GPUDescriptorHandleForHeapStart;
            skyTexDescriptor += _skyTexHeapIndex * CbvSrvUavDescriptorSize;
            CommandList.SetGraphicsRootDescriptorTable(3, skyTexDescriptor);

            CommandList.PipelineState = _psos["opaque"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

            CommandList.PipelineState = _psos["debug"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Debug]);

            CommandList.PipelineState = _psos["sky"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Sky]);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

            // Done recording commands.
            CommandList.Close();

            // Add the command list to the queue for execution.
            CommandQueue.ExecuteCommandList(CommandList);

            // Present the buffer to the screen. Presenting will automatically swap the back and front buffers.
            SwapChain.Present(0, PresentFlags.None);

            // Advance the fence value to mark commands up to this fence point.
            CurrFrameResource.Fence = ++CurrentFence;

            // Add an instruction to the command queue to set a new fence point.
            // Because we are on the GPU timeline, the new fence point won't be
            // set until the GPU finishes processing all the commands prior to this Signal().
            CommandQueue.Signal(Fence, CurrentFence);
        }

        protected override void OnMouseDown(MouseButtons button, Point location)
        {
            base.OnMouseDown(button, location);
            _lastMousePos = location;
        }

        protected override void OnMouseMove(MouseButtons button, Point location)
        {
            if ((button & MouseButtons.Left) != 0)
            {
                // Make each pixel correspond to a quarter of a degree.
                float dx = MathUtil.DegreesToRadians(0.25f * (location.X - _lastMousePos.X));
                float dy = MathUtil.DegreesToRadians(0.25f * (location.Y - _lastMousePos.Y));

                _camera.Pitch(dy);
                _camera.RotateY(dx);
            }

            _lastMousePos = location;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _shadowMap?.Dispose();
                _rootSignature?.Dispose();
                _srvDescriptorHeap?.Dispose();
                foreach (Texture texture in _textures.Values) texture.Dispose();
                foreach (FrameResource frameResource in _frameResources) frameResource.Dispose();
                foreach (MeshGeometry geometry in _geometries.Values) geometry.Dispose();
                foreach (PipelineState pso in _psos.Values) pso.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OnKeyboardInput(GameTimer gt)
        {
            float dt = gt.DeltaTime;

            if (IsKeyDown(Keys.W))
                _camera.Walk(10.0f * dt);
            if (IsKeyDown(Keys.S))
                _camera.Walk(-10.0f * dt);
            if (IsKeyDown(Keys.A))
                _camera.Strafe(-10.0f * dt);
            if (IsKeyDown(Keys.D))
                _camera.Strafe(10.0f * dt);

            _camera.UpdateViewMatrix();
        }

        private void UpdateObjectCBs()
        {
            foreach (RenderItem e in _allRitems)
            {
                // Only update the cbuffer data if the constants have changed.
                // This needs to be tracked per frame resource.
                if (e.NumFramesDirty > 0)
                {
                    var objConstants = new ObjectConstants
                    {
                        World = Matrix.Transpose(e.World),
                        TexTransform = Matrix.Transpose(e.TexTransform),
                        MaterialIndex = e.Mat.MatCBIndex
                    };
                    CurrFrameResource.ObjectCB.CopyData(e.ObjCBIndex, ref objConstants);

                    // Next FrameResource need to be updated too.
                    e.NumFramesDirty--;
                }
            }
        }

        private void UpdateMaterialBuffer()
        {
            UploadBuffer<MaterialData> currMaterialCB = CurrFrameResource.MaterialBuffer;
            foreach (Material mat in _materials.Values)
            {
                // Only update the cbuffer data if the constants have changed. If the cbuffer
                // data changes, it needs to be updated for each FrameResource.
                if (mat.NumFramesDirty > 0)
                {
                    var matConstants = new MaterialData
                    {
                        DiffuseAlbedo = mat.DiffuseAlbedo,
                        FresnelR0 = mat.FresnelR0,
                        Roughness = mat.Roughness,
                        MatTransform = Matrix.Transpose(mat.MatTransform),
                        DiffuseMapIndex = mat.DiffuseSrvHeapIndex,
                        NormalMapIndex = mat.NormalSrvHeapIndex
                    };

                    currMaterialCB.CopyData(mat.MatCBIndex, ref matConstants);

                    // Next FrameResource need to be updated too.
                    mat.NumFramesDirty--;
                }
            }
        }

        private void UpdateShadowTransform()
        {
            // Only the first "main" light casts a shadow.
            Vector3 lightDir = _rotatedLightDirections[0];
            Vector3 lightPos = -2.0f * _sceneBounds.Radius * lightDir;
            Vector3 targetPos = _sceneBounds.Center;
            Vector3 lightUp = Vector3.Up;
            Matrix lightView = Matrix.LookAtLH(lightPos, targetPos, lightUp);

            _lightPosW = lightPos;

            // Transform bounding sphere to light space.
            Vector3 sphereCenterLS = Vector3.TransformCoordinate(targetPos, lightView);

            // Ortho frustum in light space encloses scene.
            float l = sphereCenterLS.X - _sceneBounds.Radius;
            float b = sphereCenterLS.Y - _sceneBounds.Radius;
            float n = sphereCenterLS.Z - _sceneBounds.Radius;
            float r = sphereCenterLS.X + _sceneBounds.Radius;
            float t = sphereCenterLS.Y + _sceneBounds.Radius;
            float f = sphereCenterLS.Z + _sceneBounds.Radius;

            _lightNearZ = n;
            _lightFarZ = f;
            Matrix lightProj = Matrix.OrthoOffCenterLH(l, r, b, t, n, f);

            // Transform NDC space [-1,+1]^2 to texture space [0,1]^2
            var ndcToTexture = new Matrix(
                0.5f, 0.0f, 0.0f, 0.0f,
                0.0f, -0.5f, 0.0f, 0.0f,
                0.0f, 0.0f, 1.0f, 0.0f,
                0.5f, 0.5f, 0.0f, 1.0f);

            _shadowTransform = lightView * lightProj * ndcToTexture;
            _lightView = lightView;
            _lightProj = lightProj;
        }

        private void UpdateMainPassCB(GameTimer gt)
        {
            Matrix view = _camera.View;
            Matrix proj = _camera.Proj;

            Matrix viewProj = view * proj;
            Matrix invView = Matrix.Invert(view);
            Matrix invProj = Matrix.Invert(proj);
            Matrix invViewProj = Matrix.Invert(viewProj);

            _mainPassCB.View = Matrix.Transpose(view);
            _mainPassCB.InvView = Matrix.Transpose(invView);
            _mainPassCB.Proj = Matrix.Transpose(proj);
            _mainPassCB.InvProj = Matrix.Transpose(invProj);
            _mainPassCB.ViewProj = Matrix.Transpose(viewProj);
            _mainPassCB.InvViewProj = Matrix.Transpose(invViewProj);
            _mainPassCB.ShadowTransform = Matrix.Transpose(_shadowTransform);
            _mainPassCB.EyePosW = _camera.Position;
            _mainPassCB.RenderTargetSize = new Vector2(ClientWidth, ClientHeight);
            _mainPassCB.InvRenderTargetSize = 1.0f / _mainPassCB.RenderTargetSize;
            _mainPassCB.NearZ = 1.0f;
            _mainPassCB.FarZ = 1000.0f;
            _mainPassCB.TotalTime = gt.TotalTime;
            _mainPassCB.DeltaTime = gt.DeltaTime;
            _mainPassCB.AmbientLight = new Vector4(0.25f, 0.25f, 0.35f, 1.0f);
            _mainPassCB.Lights[0].Direction = _rotatedLightDirections[0];
            _mainPassCB.Lights[0].Strength = new Vector3(0.9f);
            _mainPassCB.Lights[1].Direction = _rotatedLightDirections[1];
            _mainPassCB.Lights[1].Strength = new Vector3(0.4f);
            _mainPassCB.Lights[2].Direction = _rotatedLightDirections[2];
            _mainPassCB.Lights[2].Strength = new Vector3(0.2f);

            CurrFrameResource.PassCB.CopyData(0, ref _mainPassCB);
        }

        private void UpdateShadowPassCB()
        {
            Matrix view = _lightView;
            Matrix proj = _lightProj;

            Matrix viewProj = view * proj;
            Matrix invView = Matrix.Invert(view);
            Matrix invProj = Matrix.Invert(proj);
            Matrix invViewProj = Matrix.Invert(viewProj);

            _shadowPassCB.View = Matrix.Transpose(view);
            _shadowPassCB.InvView = Matrix.Transpose(invView);
            _shadowPassCB.Proj = Matrix.Transpose(proj);
            _shadowPassCB.InvProj = Matrix.Transpose(invProj);
            _shadowPassCB.ViewProj = Matrix.Transpose(viewProj);
            _shadowPassCB.InvViewProj = Matrix.Transpose(invViewProj);
            _shadowPassCB.EyePosW = _lightPosW;
            _shadowPassCB.RenderTargetSize = new Vector2(_shadowMap.Width, _shadowMap.Height);
            _shadowPassCB.InvRenderTargetSize = 1.0f / _shadowPassCB.RenderTargetSize;
            _shadowPassCB.NearZ = _lightNearZ;
            _shadowPassCB.FarZ = _lightFarZ;

            CurrFrameResource.PassCB.CopyData(1, ref _shadowPassCB);
        }

        private void LoadTextures()
        {
            AddTexture("bricksDiffuseMap", "bricks2.dds");
            AddTexture("bricksNormalMap", "bricks2_nmap.dds");
            AddTexture("tileDiffuseMap", "tile.dds");
            AddTexture("tileNormalMap", "tile_nmap.dds");
            AddTexture("defaultDiffuseMap", "white1x1.dds");
            AddTexture("defaultNormalMap", "default_nmap.dds");
            AddTexture("skyCubeMap", "desertcube1024.dds");
        }

        private void AddTexture(string name, string filename)
        {
            var tex = new Texture
            {
                Name = name,
                Filename = $"Textures\\{filename}"
            };
            tex.Resource = TextureUtilities.CreateTextureFromDDS(Device, tex.Filename);
            _textures[tex.Name] = tex;
        }

        private void BuildRootSignature()
        {
            // Root parameter can be a table, root descriptor or root constants.
            // Perfomance TIP: Order from most frequent to least frequent.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(1, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 1), RootParameterType.ShaderResourceView),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0)),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 10, 2))
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters,
                GetStaticSamplers());

            _rootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildDescriptorHeaps()
        {
            //
            // Create the SRV heap.
            //
            var srvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = 14,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            };
            _srvDescriptorHeap = Device.CreateDescriptorHeap(srvHeapDesc);
            _descriptorHeaps = new[] { _srvDescriptorHeap };

            //
            // Fill out the heap with actual descriptors.
            //
            CpuDescriptorHandle hDescriptor = _srvDescriptorHeap.CPUDescriptorHandleForHeapStart;

            Resource[] tex2DList =
            {
                _textures["bricksDiffuseMap"].Resource,
                _textures["bricksNormalMap"].Resource,
                _textures["tileDiffuseMap"].Resource,
                _textures["tileNormalMap"].Resource,
                _textures["defaultDiffuseMap"].Resource,
                _textures["defaultNormalMap"].Resource,
            };
            Resource skyCubeMap = _textures["skyCubeMap"].Resource;

            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    ResourceMinLODClamp = 0.0f
                }
            };

            foreach (Resource tex2D in tex2DList)
            {
                srvDesc.Format = tex2D.Description.Format;
                srvDesc.Texture2D.MipLevels = tex2D.Description.MipLevels;

                Device.CreateShaderResourceView(tex2D, srvDesc, hDescriptor);

                // Next descriptor.
                hDescriptor += CbvSrvUavDescriptorSize;
            }

            srvDesc.Dimension = ShaderResourceViewDimension.TextureCube;
            srvDesc.TextureCube = new ShaderResourceViewDescription.TextureCubeResource
            {
                MostDetailedMip = 0,
                MipLevels = skyCubeMap.Description.MipLevels,
                ResourceMinLODClamp = 0.0f
            };
            srvDesc.Format = skyCubeMap.Description.Format;
            Device.CreateShaderResourceView(skyCubeMap, srvDesc, hDescriptor);

            _skyTexHeapIndex = tex2DList.Length;
            _shadowMapHeapIndex = _skyTexHeapIndex + 1;

            _nullCubeSrvIndex = _shadowMapHeapIndex + 1;

            CpuDescriptorHandle srvCpuStart = _srvDescriptorHeap.CPUDescriptorHandleForHeapStart;
            GpuDescriptorHandle srvGpuStart = _srvDescriptorHeap.GPUDescriptorHandleForHeapStart;
            CpuDescriptorHandle dsvCpuStart = DsvHeap.CPUDescriptorHandleForHeapStart;

            CpuDescriptorHandle nullSrv = srvCpuStart + _nullCubeSrvIndex * CbvSrvUavDescriptorSize;
            _nullSrv = srvGpuStart + _nullCubeSrvIndex * CbvSrvUavDescriptorSize;

            Device.CreateShaderResourceView(null, srvDesc, nullSrv);
            nullSrv += CbvSrvUavDescriptorSize;

            srvDesc.Dimension = ShaderResourceViewDimension.Texture2D;
            srvDesc.Format = Format.R8G8B8A8_UNorm;
            srvDesc.Texture2D = new ShaderResourceViewDescription.Texture2DResource
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                ResourceMinLODClamp = 0.0f
            };
            Device.CreateShaderResourceView(null, srvDesc, nullSrv);

            _shadowMap.BuildDescriptors(
                srvCpuStart + _shadowMapHeapIndex * CbvSrvUavDescriptorSize,
                srvGpuStart + _shadowMapHeapIndex * CbvSrvUavDescriptorSize,
                dsvCpuStart + DsvDescriptorSize);
        }

        private void BuildShadersAndInputLayout()
        {
            ShaderMacro[] alphaTestDefines =
            {
                new ShaderMacro("ALPHA_TEST", "1")
            };

            _shaders["standardVS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "VS", "vs_5_1");
            _shaders["opaquePS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "PS", "ps_5_1");

            _shaders["shadowVS"] = D3DUtil.CompileShader("Shaders\\Shadows.hlsl", "VS", "vs_5_1");
            _shaders["shadowOpaquePS"] = D3DUtil.CompileShader("Shaders\\Shadows.hlsl", "PS", "ps_5_1");
            _shaders["shadowAlphaTestedPS"] = D3DUtil.CompileShader("Shaders\\Shadows.hlsl", "PS", "ps_5_1", alphaTestDefines);

            _shaders["debugVS"] = D3DUtil.CompileShader("Shaders\\ShadowDebug.hlsl", "VS", "vs_5_1");
            _shaders["debugPS"] = D3DUtil.CompileShader("Shaders\\ShadowDebug.hlsl", "PS", "ps_5_1");

            _shaders["skyVS"] = D3DUtil.CompileShader("Shaders\\Sky.hlsl", "VS", "vs_5_1");
            _shaders["skyPS"] = D3DUtil.CompileShader("Shaders\\Sky.hlsl", "PS", "ps_5_1");

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
                new InputElement("TANGENT", 0, Format.R32G32B32_Float, 32, 0)
            });
        }

        private void BuildShapeGeometry()
        {
            //
            // We are concatenating all the geometry into one big vertex/index buffer. So
            // define the regions in the buffer each submesh covers.
            //

            var vertices = new List<Vertex>();
            var indices = new List<short>();

            SubmeshGeometry box = AppendMeshData(GeometryGenerator.CreateBox(1.0f, 1.0f, 1.0f, 3), vertices, indices);
            SubmeshGeometry grid = AppendMeshData(GeometryGenerator.CreateGrid(20.0f, 30.0f, 60, 40), vertices, indices);
            SubmeshGeometry sphere = AppendMeshData(GeometryGenerator.CreateSphere(0.5f, 20, 20), vertices, indices);
            SubmeshGeometry cylinder = AppendMeshData(GeometryGenerator.CreateCylinder(0.5f, 0.3f, 3.0f, 20, 20), vertices, indices);
            SubmeshGeometry quad = AppendMeshData(GeometryGenerator.CreateQuad(0.0f, 0.0f, 1.0f, 1.0f, 0.0f), vertices, indices);

            var geo = MeshGeometry.New(Device, CommandList, vertices.ToArray(), indices.ToArray(), "shapeGeo");

            geo.DrawArgs["box"] = box;
            geo.DrawArgs["grid"] = grid;
            geo.DrawArgs["sphere"] = sphere;
            geo.DrawArgs["cylinder"] = cylinder;
            geo.DrawArgs["quad"] = quad;

            _geometries[geo.Name] = geo;
        }

        private static SubmeshGeometry AppendMeshData(GeometryGenerator.MeshData meshData, List<Vertex> vertices, List<short> indices)
        {
            //
            // Define the SubmeshGeometry that cover different
            // regions of the vertex/index buffers.
            //

            var submesh = new SubmeshGeometry
            {
                IndexCount = meshData.Indices32.Count,
                StartIndexLocation = indices.Count,
                BaseVertexLocation = vertices.Count
            };

            //
            // Extract the vertex elements we are interested in and pack the
            // vertices and indices of all the meshes into one vertex/index buffer.
            //

            vertices.AddRange(meshData.Vertices.Select(vertex => new Vertex
            {
                Pos = vertex.Position,
                Normal = vertex.Normal,
                TexC = vertex.TexC,
                TangentU = vertex.TangentU
            }));
            indices.AddRange(meshData.GetIndices16());

            return submesh;
        }

        private void BuildSkullGeometry()
        {
            var vertices = new List<Vertex>();
            var indices = new List<int>();
            int vCount = 0, tCount = 0;
            using (var reader = new StreamReader("Models\\Skull.txt"))
            {
                var input = reader.ReadLine();
                if (input != null)
                    vCount = Convert.ToInt32(input.Split(':')[1].Trim());

                input = reader.ReadLine();
                if (input != null)
                    tCount = Convert.ToInt32(input.Split(':')[1].Trim());

                do
                {
                    input = reader.ReadLine();
                } while (input != null && !input.StartsWith("{", StringComparison.Ordinal));

                for (int i = 0; i < vCount; i++)
                {
                    input = reader.ReadLine();
                    if (input != null)
                    {
                        string[] vals = input.Split(' ');

                        var pos = new Vector3(
                                Convert.ToSingle(vals[0].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[1].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[2].Trim(), CultureInfo.InvariantCulture));

                        var normal = new Vector3(
                                Convert.ToSingle(vals[3].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[4].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[5].Trim(), CultureInfo.InvariantCulture));

                        // Generate a tangent vector so normal mapping works.  We aren't applying
                        // a texture map to the skull, so we just need any tangent vector so that
                        // the math works out to give us the original interpolated vertex normal.
                        Vector3 tangent = Math.Abs(Vector3.Dot(normal, Vector3.Up)) < 1.0f - 0.001f
                            ? Vector3.Normalize(Vector3.Cross(normal, Vector3.Up))
                            : Vector3.Normalize(Vector3.Cross(normal, Vector3.ForwardLH));

                        vertices.Add(new Vertex
                        {
                            Pos = pos,
                            Normal = normal,
                            TangentU = tangent
                        });
                    }
                }

                do
                {
                    input = reader.ReadLine();
                } while (input != null && !input.StartsWith("{", StringComparison.Ordinal));

                for (var i = 0; i < tCount; i++)
                {
                    input = reader.ReadLine();
                    if (input == null)
                    {
                        break;
                    }
                    var m = input.Trim().Split(' ');
                    indices.Add(Convert.ToInt32(m[0].Trim()));
                    indices.Add(Convert.ToInt32(m[1].Trim()));
                    indices.Add(Convert.ToInt32(m[2].Trim()));
                }
            }

            var geo = MeshGeometry.New(Device, CommandList, vertices.ToArray(), indices.ToArray(), "skullGeo");
            var submesh = new SubmeshGeometry
            {
                IndexCount = indices.Count,
                StartIndexLocation = 0,
                BaseVertexLocation = 0
            };

            geo.DrawArgs["skull"] = submesh;

            _geometries[geo.Name] = geo;
        }

        private void BuildPSOs()
        {
            //
            // PSO for opaque objects.
            //

            var opaquePsoDesc = new GraphicsPipelineStateDescription
            {
                InputLayout = _inputLayout,
                RootSignature = _rootSignature,
                VertexShader = _shaders["standardVS"],
                PixelShader = _shaders["opaquePS"],
                RasterizerState = RasterizerStateDescription.Default(),
                BlendState = BlendStateDescription.Default(),
                DepthStencilState = DepthStencilStateDescription.Default(),
                SampleMask = unchecked((int)uint.MaxValue),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality),
                DepthStencilFormat = DepthStencilFormat,
                StreamOutput = new StreamOutputDescription() //find out how this should actually be done later
            };
            opaquePsoDesc.RenderTargetFormats[0] = BackBufferFormat;
            _psos["opaque"] = Device.CreateGraphicsPipelineState(opaquePsoDesc);

            //
            // PSO for shadow map pass.
            //

            var smapPsoDesc = opaquePsoDesc.Copy();
            smapPsoDesc.RasterizerState.DepthBias = 100000;
            smapPsoDesc.RasterizerState.DepthBiasClamp = 0.0f;
            smapPsoDesc.RasterizerState.SlopeScaledDepthBias = 1.0f;
            smapPsoDesc.VertexShader = _shaders["shadowVS"];
            smapPsoDesc.PixelShader = _shaders["shadowOpaquePS"];
            // Shadow map pass does not have a render target.
            smapPsoDesc.RenderTargetFormats[0] = Format.Unknown;
            smapPsoDesc.RenderTargetCount = 0;
            _psos["shadow_opaque"] = Device.CreateGraphicsPipelineState(smapPsoDesc);

            //
            // PSO for debug layer.
            //

            var debugPsoDesc = opaquePsoDesc.Copy();
            debugPsoDesc.VertexShader = _shaders["debugVS"];
            debugPsoDesc.PixelShader = _shaders["debugPS"];
            _psos["debug"] = Device.CreateGraphicsPipelineState(debugPsoDesc);

            //
            // PSO for sky.
            //

            GraphicsPipelineStateDescription skyPsoDesc = opaquePsoDesc.Copy();
            // The camera is inside the sky sphere, so just turn off culling.
            skyPsoDesc.RasterizerState.CullMode = CullMode.None;
            // Make sure the depth function is LESS_EQUAL and not just LESS.
            // Otherwise, the normalized depth values at z = 1 (NDC) will
            // fail the depth test if the depth buffer was cleared to 1.
            skyPsoDesc.DepthStencilState.DepthComparison = Comparison.LessEqual;
            skyPsoDesc.VertexShader = _shaders["skyVS"];
            skyPsoDesc.PixelShader = _shaders["skyPS"];
            _psos["sky"] = Device.CreateGraphicsPipelineState(skyPsoDesc);
        }

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 2, _allRitems.Count, _materials.Count));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        private void BuildMaterials()
        {
            AddMaterial(new Material
            {
                Name = "bricks0",
                MatCBIndex = 0,
                DiffuseSrvHeapIndex = 0,
                NormalSrvHeapIndex = 1,
                DiffuseAlbedo = Vector4.One,
                FresnelR0 = new Vector3(0.1f),
                Roughness = 0.3f
            });
            AddMaterial(new Material
            {
                Name = "tile0",
                MatCBIndex = 1,
                DiffuseSrvHeapIndex = 2,
                NormalSrvHeapIndex = 3,
                DiffuseAlbedo = new Vector4(0.9f, 0.9f, 0.9f, 1.0f),
                FresnelR0 = new Vector3(0.2f),
                Roughness = 0.1f
            });
            AddMaterial(new Material
            {
                Name = "mirror0",
                MatCBIndex = 2,
                DiffuseSrvHeapIndex = 4,
                NormalSrvHeapIndex = 5,
                DiffuseAlbedo = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                FresnelR0 = new Vector3(0.98f, 0.97f, 0.95f),
                Roughness = 0.1f
            });
            AddMaterial(new Material
            {
                Name = "skullMat",
                MatCBIndex = 3,
                DiffuseSrvHeapIndex = 4,
                NormalSrvHeapIndex = 5,
                DiffuseAlbedo = new Vector4(0.3f, 0.3f, 0.3f, 1.0f),
                FresnelR0 = new Vector3(0.6f),
                Roughness = 0.2f
            });
            AddMaterial(new Material
            {
                Name = "sky",
                MatCBIndex = 4,
                DiffuseSrvHeapIndex = 6,
                NormalSrvHeapIndex = 7,
                DiffuseAlbedo = Vector4.One,
                FresnelR0 = new Vector3(0.1f),
                Roughness = 1.0f
            });

        }

        private void AddMaterial(Material mat) => _materials[mat.Name] = mat;

        private void BuildRenderItems()
        {
            AddRenderItem(RenderLayer.Sky, 0, "sky", "shapeGeo", "sphere",
                world: Matrix.Scaling(5000.0f));
            AddRenderItem(RenderLayer.Debug, 1, "bricks0", "shapeGeo", "quad");
            AddRenderItem(RenderLayer.Opaque, 2, "bricks0", "shapeGeo", "box",
                world: Matrix.Scaling(2.0f, 1.0f, 2.0f) * Matrix.Translation(0.0f, 0.5f, 0.0f),
                texTransform: Matrix.Scaling(1.0f, 0.5f, 1.0f));
            AddRenderItem(RenderLayer.Opaque, 3, "skullMat", "skullGeo", "skull",
                world: Matrix.Scaling(0.4f) * Matrix.Translation(0.0f, 1.0f, 0.0f));
            AddRenderItem(RenderLayer.Opaque, 4, "tile0", "shapeGeo", "grid",
                texTransform: Matrix.Scaling(8.0f, 8.0f, 1.0f));

            Matrix brickTexTransform = Matrix.Scaling(1.5f, 2.0f, 1.0f);
            int objCBIndex = 5;
            for (int i = 0; i < 5; ++i)
            {
                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "bricks0", "shapeGeo", "cylinder",
                    world: Matrix.Translation(-5.0f, 1.5f, -10.0f + i * 5.0f),
                    texTransform: brickTexTransform);
                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "bricks0", "shapeGeo", "cylinder",
                    world: Matrix.Translation(+5.0f, 1.5f, -10.0f + i * 5.0f),
                    texTransform: brickTexTransform);

                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "mirror0", "shapeGeo", "sphere",
                    world: Matrix.Translation(-5.0f, 3.5f, -10.0f + i * 5.0f));
                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "mirror0", "shapeGeo", "sphere",
                    world: Matrix.Translation(+5.0f, 3.5f, -10.0f + i * 5.0f));
            }
        }

        private void AddRenderItem(RenderLayer layer, int objCBIndex, string matName, string geoName, string submeshName,
            Matrix? world = null, Matrix? texTransform = null)
        {
            MeshGeometry geo = _geometries[geoName];
            SubmeshGeometry submesh = geo.DrawArgs[submeshName];
            var renderItem = new RenderItem
            {
                ObjCBIndex = objCBIndex,
                Mat = _materials[matName],
                Geo = geo,
                IndexCount = submesh.IndexCount,
                StartIndexLocation = submesh.StartIndexLocation,
                BaseVertexLocation = submesh.BaseVertexLocation,
                World = world ?? Matrix.Identity,
                TexTransform = texTransform ?? Matrix.Identity
            };
            _ritemLayers[layer].Add(renderItem);
            _allRitems.Add(renderItem);
        }

        private void DrawRenderItems(GraphicsCommandList cmdList, List<RenderItem> ritems)
        {
            int objCBByteSize = D3DUtil.CalcConstantBufferByteSize<ObjectConstants>();

            Resource objectCB = CurrFrameResource.ObjectCB.Resource;

            foreach (RenderItem ri in ritems)
            {
                cmdList.SetVertexBuffer(0, ri.Geo.VertexBufferView);
                cmdList.SetIndexBuffer(ri.Geo.IndexBufferView);
                cmdList.PrimitiveTopology = ri.PrimitiveType;

                long objCBAddress = objectCB.GPUVirtualAddress + ri.ObjCBIndex * objCBByteSize;

                cmdList.SetGraphicsRootConstantBufferView(0, objCBAddress);

                cmdList.DrawIndexedInstanced(ri.IndexCount, 1, ri.StartIndexLocation, ri.BaseVertexLocation, 0);
            }
        }

        private void DrawSceneToShadowMap()
        {
            CommandList.SetViewport(_shadowMap.Viewport);
            CommandList.SetScissorRectangles(_shadowMap.ScissorRectangle);

            // Change to DEPTH_WRITE.
            CommandList.ResourceBarrierTransition(_shadowMap.Resource, ResourceStates.GenericRead, ResourceStates.DepthWrite);

            int passCBByteSize = D3DUtil.CalcConstantBufferByteSize<PassConstants>();

            // Clear the depth buffer.
            CommandList.ClearDepthStencilView(_shadowMap.Dsv, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Set null render target because we are only going to draw to
            // depth buffer. Setting a null render target will disable color writes.
            // Note the active PSO also must specify a render target count of 0.
            CommandList.SetRenderTargets((CpuDescriptorHandle?)null, _shadowMap.Dsv);

            // Bind the pass constant buffer for shadow map pass.
            Resource passCB = CurrFrameResource.PassCB.Resource;
            long passCBAddress = passCB.GPUVirtualAddress + passCBByteSize;
            CommandList.SetGraphicsRootConstantBufferView(1, passCBAddress);

            CommandList.PipelineState = _psos["shadow_opaque"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

            // Change back to GENERIC_READ so we can read the texture in a shader.
            CommandList.ResourceBarrierTransition(_shadowMap.Resource, ResourceStates.DepthWrite, ResourceStates.GenericRead);
        }

        // Applications usually only need a handful of samplers. So just define them all up front
        // and keep them available as part of the root signature.
        private static StaticSamplerDescription[] GetStaticSamplers() => new[]
        {
            // PointWrap
            new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
            {
                Filter = Filter.MinMagMipPoint,
                AddressUVW = TextureAddressMode.Wrap
            },
            // PointClamp
            new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
            {
                Filter = Filter.MinMagMipPoint,
                AddressUVW = TextureAddressMode.Clamp
            },
            // LinearWrap
            new StaticSamplerDescription(ShaderVisibility.All, 2, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressUVW = TextureAddressMode.Wrap
            },
            // LinearClamp
            new StaticSamplerDescription(ShaderVisibility.All, 3, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressUVW = TextureAddressMode.Clamp
            },
            // AnisotropicWrap
            new StaticSamplerDescription(ShaderVisibility.All, 4, 0)
            {
                Filter = Filter.Anisotropic,
                AddressUVW = TextureAddressMode.Wrap,
                MaxAnisotropy = 8
            },
            // AnisotropicClamp
            new StaticSamplerDescription(ShaderVisibility.All, 5, 0)
            {
                Filter = Filter.Anisotropic,
                AddressUVW = TextureAddressMode.Clamp,
                MaxAnisotropy = 8
            },
            // Shadow
            new StaticSamplerDescription(ShaderVisibility.All, 6, 0)
            {
                Filter = Filter.ComparisonMinMagLinearMipPoint,
                AddressUVW = TextureAddressMode.Border,
                MaxAnisotropy = 16,
                ComparisonFunc = Comparison.LessEqual,
                BorderColor = StaticBorderColor.OpaqueBlack
            }
        };
    }
}
