# Third-party notices

RE2 Studio is licensed under the GNU General Public License v3.0 (see [LICENSE](LICENSE)). It uses the following third-party components. Each is distributed under its own license, and those licenses are compatible with GPL 3.0. The release builds bundle the runtime components listed here.

## Runtime components

| Component | License | Project |
|---|---|---|
| SharpGLTF (Core, Runtime, Toolkit) 1.0.5 | MIT | https://github.com/vpenades/SharpGLTF |
| SixLabors.ImageSharp 2.1.11 | Apache License 2.0 | https://github.com/SixLabors/ImageSharp |
| Silk.NET 2.23.0 (Windowing, Input, OpenGL, OpenGL.Extensions.ImGui, GLFW) | MIT | https://github.com/dotnet/Silk.NET |
| ImGui.NET / cimgui / Dear ImGui | MIT | https://github.com/ImGuiNET/ImGui.NET, https://github.com/ocornut/imgui |
| GLFW (via Ultz.Native.GLFW) | zlib | https://www.glfw.org |
| .NET runtime (included in self-contained builds) | MIT | https://github.com/dotnet/runtime |

## Test-only components

These components are not included in release builds.

| Component | License | Project |
|---|---|---|
| xUnit | Apache License 2.0 | https://github.com/xunit/xunit |
| coverlet.collector | MIT | https://github.com/coverlet-coverage/coverlet |
| Microsoft.NET.Test.Sdk | MIT | https://github.com/microsoft/vstest |

## Optional external tool

**FFmpeg** is not bundled or linked. If it is installed and on your `PATH`, RE2 Studio runs it as a separate program for FMV playback and conversion. FFmpeg is licensed under the LGPL/GPL (https://ffmpeg.org/legal.html).

## Full license texts

- MIT, Apache 2.0 and zlib license texts are included in each package's NuGet distribution and in its source repository linked above.
- Copyright notices remain with their respective authors.

## Game content

*Resident Evil* and all related names, characters and assets are the property of Capcom. This repository contains no game data, and its license grants no rights to Capcom's material.
