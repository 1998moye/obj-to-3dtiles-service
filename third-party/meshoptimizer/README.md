# meshoptimizer dependency

This directory records the licensing and provenance of the meshoptimizer dependency used by the
custom HLOD implementation. The dependency source and binaries are intentionally not vendored in
this repository.

`third-party/Obj2Tiles/Obj2Tiles/Obj2Tiles.csproj` pins `Meshoptimizer.NET` version `1.0.7`. During
`dotnet restore`, NuGet supplies:

- the Meshoptimizer.NET managed binding;
- `runtimes/linux-x64/native/libmeshoptimizer.so` for the Docker image;
- `runtimes/win-x64/native/meshoptimizer.dll` for Windows publishing.

The HLOD stage calls the upstream `meshopt_simplify` and
`meshopt_simplifyWithAttributes` C ABI directly from `MeshOptimizerSimplifier.cs`.

Upstream projects:

- Meshoptimizer.NET: https://github.com/BoyBaykiller/Meshoptimizer.NET
- meshoptimizer: https://github.com/zeux/meshoptimizer

Both projects are MIT licensed. Their license texts are retained in this directory. These
third-party materials are not covered by the repository root's non-commercial license.
