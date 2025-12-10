# Photon Render System 2

A high-performance ray-traced global illumination system for Unity Universal Render Pipeline (URP).

## Overview

Photon Render System 2 is a comprehensive ray tracing solution that provides real-time global illumination, reflections, and advanced lighting features. Built on Unity's Universal Render Pipeline, it leverages modern GPU ray tracing capabilities to deliver photorealistic rendering.

## Showcase
<img width="2210" height="1224" alt="92b49b680e7724c588dbe23e35ac2949 - 副本" src="https://github.com/user-attachments/assets/bb9a1b5d-6aa6-4bf5-b591-b19ed0bf794f" />
<img width="2204" height="1233" alt="c61ef9c68d764163cd6be768beef4ad6 - 副本" src="https://github.com/user-attachments/assets/ef160377-72fc-4af9-b8c3-8fcd2f2e4ed2" />
<img width="2195" height="1229" alt="5b0a4405-e6da-4132-b03f-90a389d7faa6" src="https://github.com/user-attachments/assets/2d901ecd-f37a-45ae-abb6-8b9d19fbfde0" />
<img width="2179" height="1211" alt="3fb3eea9e38be5648d8d13b879538579" src="https://github.com/user-attachments/assets/845c9696-3d11-4255-bca1-7d426cdc76cc" />
<img width="2184" height="1201" alt="0f714abd-ad08-4899-b730-56189a73080e" src="https://github.com/user-attachments/assets/9e3050b4-e584-4c00-bb0d-c77100ca4a22" />
<img width="2184" height="1215" alt="2dbadc86-16e0-486f-830a-bcb133870f55" src="https://github.com/user-attachments/assets/d162c06e-a654-4e98-b55d-1996a88507ab" />
<img width="2208" height="1228" alt="6f06d85c14126103bd31d45a040900a5" src="https://github.com/user-attachments/assets/667a4f71-e342-462d-9130-64f4812635c9" />
<img width="2205" height="1228" alt="ec1c4f3fc14f4baca46f8ad76fae91a7" src="https://github.com/user-attachments/assets/7fb0cefc-fc8d-4369-b93d-0deeef2cb399" />
![Uploading 249f4d2ca67e87ebc3eb831f4910ee0c.png…]()

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
