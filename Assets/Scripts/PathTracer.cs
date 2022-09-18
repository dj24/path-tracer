using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PathTracer : ScriptableRendererFeature
{
    private PathTracerPass _pathTracerPass;
    class PathTracerPass : ScriptableRenderPass
    {
        private RenderTargetIdentifier _colorBuffer;
        string m_ProfilerTag = "PathTracerPass";
        private ComputeShader _computeShader;
        private RenderTexture _sceneTexture, _outputTexture;
        public PathTracerPass()
        {
            _computeShader = (ComputeShader)Resources.Load("Compute/PathTracer");
        }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.depthBufferBits = 0;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            _colorBuffer = renderingData.cameraData.renderer.cameraColorTarget;
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // Init input and output texture
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            _sceneTexture = RenderTexture.GetTemporary(descriptor);
            descriptor.enableRandomWrite = true;
            _outputTexture = RenderTexture.GetTemporary(descriptor);
            
            // Copy scene buffer into texture
            var cmd = CommandBufferPool.Get(m_ProfilerTag);
            cmd.Blit(_colorBuffer,_sceneTexture);
            
            // Execute compute
            _computeShader.SetTexture(0, "Result", _outputTexture);
            _computeShader.SetTexture(0, "Scene", _sceneTexture);
            _computeShader.Dispatch(0, _outputTexture.width, _outputTexture.height, 1);
            
            // Sync compute with frame
            var request = AsyncGPUReadback.Request(_outputTexture);
            request.WaitForCompletion();
            
            // Clean up
            RenderTexture.ReleaseTemporary(_outputTexture);
            RenderTexture.ReleaseTemporary(_sceneTexture);
            
            // Copy processed texture into scene buffer
            cmd.Blit(_outputTexture,_colorBuffer);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    public override void Create()
    {
        _pathTracerPass = new PathTracerPass();
        _pathTracerPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pathTracerPass);
    }
}
