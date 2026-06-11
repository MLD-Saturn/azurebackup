```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8655/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3


```
| Method                                         | FileSizeMB | DedupRatio | Mean        | Error     | StdDev    | Median      | Allocated |
|----------------------------------------------- |----------- |----------- |------------:|----------:|----------:|------------:|----------:|
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **4**          | **0**          |    **24.62 ms** |  **0.208 ms** |  **0.194 ms** |    **24.57 ms** |   **1.01 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **4**          | **1**          |    **24.95 ms** |  **0.499 ms** |  **0.937 ms** |    **24.49 ms** |   **1.01 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **64**         | **0**          |   **391.03 ms** |  **1.855 ms** |  **1.735 ms** |   **390.98 ms** |   **1.08 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **64**         | **1**          |   **389.08 ms** |  **0.904 ms** |  **0.802 ms** |   **389.10 ms** |   **1.09 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **256**        | **0**          | **1,540.72 ms** |  **7.298 ms** |  **6.827 ms** | **1,538.07 ms** | **327.32 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **256**        | **1**          | **1,520.07 ms** | **12.935 ms** | **11.467 ms** | **1,514.51 ms** |   **1.39 MB** |
