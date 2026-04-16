```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-TOWPHV : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

IterationCount=3  LaunchCount=1  WarmupCount=2  

```
| Method                          | TotalChunks | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0       | Gen1       | Gen2       | Allocated | Alloc Ratio |
|-------------------------------- |------------ |----------:|-----------:|----------:|------:|--------:|-----------:|-----------:|-----------:|----------:|------------:|
| **&#39;Phase5: reverse-index lookup&#39;**  | **10000**       |  **77.63 ms** |  **27.763 ms** |  **1.522 ms** |  **1.60** |    **0.04** |  **3142.8571** |  **3142.8571** |  **3142.8571** |   **11.8 MB** |        **0.21** |
| &#39;Legacy: full chunk-index scan&#39; | 10000       |  48.66 ms |  18.937 ms |  1.038 ms |  1.00 |    0.03 |  7363.6364 |  1545.4545 |  1000.0000 |   56.1 MB |        1.00 |
|                                 |             |           |            |           |       |         |            |            |            |           |             |
| **&#39;Phase5: reverse-index lookup&#39;**  | **50000**       | **365.47 ms** | **252.862 ms** | **13.860 ms** |  **1.67** |    **0.05** | **16000.0000** | **16000.0000** | **16000.0000** |  **64.26 MB** |        **0.26** |
| &#39;Legacy: full chunk-index scan&#39; | 50000       | 218.92 ms |   4.134 ms |  0.227 ms |  1.00 |    0.00 | 35000.0000 |  6666.6667 |  1000.0000 | 250.94 MB |        1.00 |
