# Photon Render System 2

A high-performance ray-traced global illumination system for Unity Universal Render Pipeline (URP).

## Overview

Photon Render System 2 is a comprehensive ray tracing solution that provides real-time global illumination, reflections, and advanced lighting features. Built on Unity's Universal Render Pipeline, it leverages modern GPU ray tracing capabilities to deliver photorealistic rendering.

## Features

### Core Rendering
- **Ray-Traced Global Illumination**: Full-resolution primary ray tracing with G-buffer generation
- **BRDF Ray Tracing**: Physically-based bidirectional reflectance distribution function ray tracing
- **ReSTIR Integration**: Reservoir-based Spatiotemporal Importance Resampling for efficient light sampling
- **Multi-Bounce Lighting**: Configurable bounce count for accurate indirect lighting

### Denoising
- **DLSS Support**: NVIDIA DLSS integration for high-quality upscaling and denoising
- **SVGF Denoising**: Spatiotemporal Variance-Guided Filtering for temporal stability
- **Motion Vector Support**: Accurate motion vectors for temporal denoising

### Performance Optimizations
- **BVH Acceleration**: BVH8 acceleration structure for fast ray-scene intersection
- **Resolution Scaling**: Configurable resolution downscaling (1x, 2x, 4x, 8x) for performance
- **Dynamic Resource Management**: Efficient render texture and compute buffer pooling
- **Quality Presets**: Pre-configured quality settings (Very Low to Very High)

### Material System
- **PhotonLit Shader**: Physically-based material shader with full PBR support
- **Metallic Workflow**: Standard metallic/roughness material properties
- **Anisotropy Support**: Anisotropic reflection support
- **Transparency**: Multi-iteration transparent material support

### Lighting
- **Main Light Integration**: Direct sunlight/directional light support with configurable angular diameter
- **Skybox Lighting**: Environment map lighting with exposure control
- **Perfect Mirror Reflections**: Configurable iteration count for mirror surfaces

## Requirements

- **Unity Version**: Unity 6000
- **Graphics API**: DirectX 12 with ray tracing support
- **GPU**: NVIDIA RTX series
- **Platform**: Windows (DirectX 12)

## Installation

1. Clone or download this repository
2. Copy the `PhotonGISystem2` folder to your Unity project's `Assets` directory
3. Ensure your project is using Universal Render Pipeline (URP)
4. Add the `PhotonRendererFeature` to your URP Renderer asset
5. Place the `PhotonRenderSystemManager` prefab in your scene

## Quick Start

### Setup

1. **Add Renderer Feature**:
   - Open your URP Renderer asset
   - Add `PhotonRendererFeature` to the renderer features list

2. **Scene Setup**:
   - Add the `PhotonRenderSystemManager` prefab to your scene
   - Configure quality settings in the manager component

3. **Material Setup**:
   - Use the `PhotonSystem/PhotonLitShader` shader for materials that should receive ray-traced lighting
   - Configure material properties as needed (metallic, smoothness, etc.)

### Configuration

The system includes several quality presets:
- **Very Low Quality**: Minimal ray tracing for testing
- **Low Quality**: Basic ray tracing with reduced samples
- **Medium Quality**: Balanced quality and performance
- **High Quality**: High-quality ray tracing
- **Very High Quality**: Maximum quality settings

You can also create custom quality configurations through the `PhotonRenderSystemManager`.

## System Architecture

### Core Managers

- **PhotonRenderSystemManager**: Central configuration manager for quality presets
- **PhotonRenderManager**: Main rendering orchestrator
- **RayTraceManager**: Manages RTAS, geometry buffers, and environment capture
- **ResourceManager**: Handles shader and compute shader resources
- **RTManager**: Render texture and compute buffer pooling
- **DlssDenoiserManager**: DLSS denoising integration
- **SvgfDenoiserManager**: SVGF temporal denoising

### Rendering Pipeline

1. **Primary Ray Pass**: Full-resolution G-buffer generation with ray tracing
2. **BRDF Ray Tracing**: Secondary ray tracing for indirect lighting
3. **ReSTIR Resampling**: Temporal and spatial resampling for efficient light transport
4. **Denoising**: DLSS and/or SVGF denoising passes
5. **Compositing**: Final composite of direct and indirect lighting

## Configuration Options

### BRDF Ray Tracing Settings
- **Samples Per Pixel**: Number of light samples per pixel (1-256)
- **Max Bounces**: Maximum ray bounce count (1-16)
- **Resolution Downscale**: Render resolution multiplier (1x, 2x, 4x, 8x)
- **Importance Sampling**: Enable/disable importance sampling
- **Temporal Resampling**: Enable/disable ReSTIR temporal resampling
- **Spatial Resampling**: Enable/disable ReSTIR spatial resampling

### Main Light Settings
- **Enabled**: Toggle main directional light
- **Angular Diameter**: Sun disk size (0.01-10 degrees)
- **Transparent Iterations**: Ray iterations for transparent materials
- **Direct Light Multiplier**: Intensity multiplier for direct light

### Ray Tracing Settings
- **Max Iterations**: Maximum iterations for perfect mirror reflections (1-20)
- **Skybox Exposure**: Environment map exposure multiplier (0-10)

## Known Limitations

- Requires hardware ray tracing support
- Performance scales with scene complexity and ray tracing settings
- Some features may not be available on all platforms

## Contributing

Contributions are welcome! Please feel free to submit pull requests or open issues for bugs and feature requests.

## Acknowledgments

Built for Unity Universal Render Pipeline with ray tracing support.
