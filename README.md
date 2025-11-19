This repository contains a university project that aims to find ways of improving the PNG format, and a light framework for comparing the custom algorithm with PNG and WEBP.

### How to use
Install VS2022 (or your editor of choice) and have the .NET 8 SDK. Then just build and run. 

The solution alaready contains a small mix of photographic and computer graphics samples in order to assess the performance over multiple types of images. You can extend your tests by adding addition files to the __images__ folder.

Note: The project uses the SixLabors.ImageSharp and ZstdSharp.Port nuget packages for image manipulation

### Iteration 1
Implemented a PNG-like filter, and replaced DEFLATE with ZSTD compression.
