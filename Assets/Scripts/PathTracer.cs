using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public enum SamplesPerPixel
{
    One = 1,
    Two = 2,
    Four = 4,
    Eight = 8,
    Sixteen = 16
}

public enum PathTracerKernel
{
    Mix,
    OneSample, 
    TwoSamples,
    FourSamples,
    EightSamples,
    SixteenSamples,
}

public class PathTracer : ScriptableRendererFeature
{
    private PathTracerPass _pathTracerPass;
    [Range(1,16)] public int downscaleFactor = 1;
    [Range(0.0f,1.0f)] public float blendAmount;
    public SamplesPerPixel samplesPerPixel;
    public bool useAccumulation;
    class PathTracerPass : ScriptableRenderPass
    {
        private RenderTargetIdentifier _colorBuffer, _depthBuffer;
        private const string ProfilerTag = "PathTracerPass";
        private readonly ComputeShader _computeShader;
        private readonly int _downscaleFactor;
        private readonly float _blendAmount;
        private float _verticalFov;
        private Vector3 _cameraDirection;
        private readonly int _samplesPerPixel;
        private readonly bool _useAccumulation;
        private readonly int _vertexStride;

        private int _kernelIndex
        {
            get
            {
                switch (_samplesPerPixel)
                {
                    case (int)SamplesPerPixel.Two:
                        return (int) PathTracerKernel.TwoSamples;
                    case (int)SamplesPerPixel.Four:
                        return (int) PathTracerKernel.FourSamples;
                    case (int)SamplesPerPixel.Eight:
                        return (int) PathTracerKernel.EightSamples;
                    case (int)SamplesPerPixel.Sixteen:
                        return (int) PathTracerKernel.SixteenSamples;
                    default:
                        return (int) PathTracerKernel.OneSample;
                }
            }
        }

        public PathTracerPass(int downscaleFactor, float blendAmount, int samplesPerPixel, bool useAccumulation)
        {
            _useAccumulation = useAccumulation;
            _blendAmount = blendAmount;
            _samplesPerPixel = samplesPerPixel;
            _downscaleFactor = downscaleFactor == 0 ? 1 : downscaleFactor;
            _computeShader = (ComputeShader)Resources.Load("Compute/PathTracer");
        }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            _verticalFov = renderingData.cameraData.camera.fieldOfView;
            _cameraDirection = -renderingData.cameraData.camera.transform.forward;

            // RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            // descriptor.depthBufferBits = 32;
            // ConfigureInput(ScriptableRenderPassInput.Depth);
            _colorBuffer = renderingData.cameraData.renderer.cameraColorTarget;
            _depthBuffer = renderingData.cameraData.renderer.cameraDepthTarget;
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (renderingData.cameraData.cameraType == CameraType.Preview ||
                renderingData.cameraData.cameraType == CameraType.Reflection)
            {
                return;
            }

            var reflectionProbe = Camera.main.GetComponent<ReflectionProbe>();
            if (reflectionProbe.texture == null)
            {
                return;
            }
            var meshFilters = FindObjectsOfType(typeof(MeshFilter)) as MeshFilter[];
            if (meshFilters.Length == 0)
            {
                return;
            }
            
            // renderingData.cameraData.camera.depthTextureMode = DepthTextureMode.Depth;

            var mesh = meshFilters[0].sharedMesh;
            // meshFilters[0].GetComponent<Renderer>().enabled = false;
            
            mesh.indexBufferTarget = GraphicsBuffer.Target.Raw;
            mesh.vertexBufferTarget = GraphicsBuffer.Target.Raw;
            var vertexBuffer = mesh.GetVertexBuffer(0);
            var indexBuffer = mesh.GetIndexBuffer();
            var triangleCount = (int)(meshFilters[0].sharedMesh.GetIndexCount(0) / 3);

            // Init input and output texture
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            var _sceneTexture = RenderTexture.GetTemporary(descriptor);
            var _depthTexture = RenderTexture.GetTemporary(descriptor);
            var _upscaleTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.enableRandomWrite = true;
            var _outputTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.width /= _downscaleFactor;
            descriptor.height /= _downscaleFactor;
            var _downscaleTexture = RenderTexture.GetTemporary(descriptor);
            
            // Copy scene buffer into texture
            var cmd = CommandBufferPool.Get(ProfilerTag);
            cmd.Blit(_depthBuffer,_depthTexture);
            cmd.Blit(_colorBuffer,_sceneTexture);

            // Execute compute
            _computeShader.SetBuffer(_kernelIndex,"VertexBuffer",vertexBuffer);
            _computeShader.SetBuffer(_kernelIndex,"IndexBuffer",indexBuffer);
            _computeShader.SetBool("UseAccumulation", _useAccumulation);
            _computeShader.SetInt("Width", _downscaleTexture.width);
            _computeShader.SetVector("MeshOrigin", meshFilters[0].transform.position);
            _computeShader.SetMatrix("TransformMatrix", meshFilters[0].GetComponent<Renderer>().worldToLocalMatrix);
            _computeShader.SetInt("TriangleCount", triangleCount);
            _computeShader.SetInt("VertexStride", vertexBuffer.stride);
            _computeShader.SetInt("Height", _downscaleTexture.height);
            _computeShader.SetInt("DownscaleFactor", _downscaleFactor);
            _computeShader.SetInt("SamplesPerPixel", _samplesPerPixel);
            _computeShader.SetFloat("VerticalFov", _verticalFov);
            _computeShader.SetVector("CameraDirection", new Vector4(_cameraDirection.x, _cameraDirection.y, _cameraDirection.z, 0));

            _computeShader.SetTexture(_kernelIndex, Shader.PropertyToID("Downscale"), _downscaleTexture);
            _computeShader.SetTexture(_kernelIndex, Shader.PropertyToID("Depth"), _depthTexture);
            _computeShader.SetTexture(_kernelIndex, Shader.PropertyToID("Skybox"), reflectionProbe.texture);
            uint kernelX = 1;
            uint kernelY = 1;
            // _computeShader.GetKernelThreadGroupSizes(_kernelIndex, out kernelX,out kernelY, out _);
            int threadGroupsX = (int) (_downscaleTexture.width / kernelX); 
            int threadGroupsY = (int) (_downscaleTexture.height / kernelY);
            _computeShader.Dispatch(_kernelIndex, threadGroupsX, threadGroupsY, 1);
            
            AsyncGPUReadback.Request(_outputTexture).WaitForCompletion();
            
            // Clean up
            RenderTexture.ReleaseTemporary(_sceneTexture);
            RenderTexture.ReleaseTemporary(_downscaleTexture);
            RenderTexture.ReleaseTemporary(_depthTexture);
            vertexBuffer.Release();
            indexBuffer.Release();
            
            cmd.Blit(_downscaleTexture,_upscaleTexture);

            _computeShader.SetTexture((int)PathTracerKernel.Mix, Shader.PropertyToID("Result"), _outputTexture);
            _computeShader.SetTexture((int)PathTracerKernel.Mix, Shader.PropertyToID("Upscale"), _upscaleTexture);
            _computeShader.SetTexture((int)PathTracerKernel.Mix, Shader.PropertyToID("Scene"), _sceneTexture);
            _computeShader.Dispatch((int)PathTracerKernel.Mix, _sceneTexture.width, _sceneTexture.height, 1);
            
            // Sync compute with frame
            AsyncGPUReadback.Request(_outputTexture).WaitForCompletion();


            // Clean up
            RenderTexture.ReleaseTemporary(_outputTexture);
            RenderTexture.ReleaseTemporary(_upscaleTexture);
            
            // Copy processed texture into scene buffer
            cmd.Blit(_outputTexture,_colorBuffer);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    public override void Create()
    {
        _pathTracerPass = new PathTracerPass(downscaleFactor, blendAmount, (int)samplesPerPixel, useAccumulation);
        _pathTracerPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pathTracerPass);
    }
}
