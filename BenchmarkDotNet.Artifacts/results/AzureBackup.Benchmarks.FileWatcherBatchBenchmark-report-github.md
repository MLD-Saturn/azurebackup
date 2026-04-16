```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                               | ChangeCount | Mean          | Error | Ratio | Gen0          | Gen1         | Gen2         | Allocated    | Alloc Ratio |
|------------------------------------- |------------ |--------------:|------:|------:|--------------:|-------------:|-------------:|-------------:|------------:|
| **&#39;Legacy: per-change QueueFileChange&#39;** | **100**         |     **993.41 ms** |    **NA** |  **1.00** |     **3000.0000** |    **3000.0000** |    **3000.0000** |     **27.42 MB** |        **1.00** |
| &#39;Phase4: QueueFileChangesBatch&#39;      | 100         |      30.08 ms |    NA |  0.03 |             - |            - |            - |      1.48 MB |        0.05 |
|                                      |             |               |       |       |               |              |              |              |             |
| **&#39;Legacy: per-change QueueFileChange&#39;** | **1000**        |  **11,041.34 ms** |    **NA** | **1.000** |   **193000.0000** |  **110000.0000** |  **105000.0000** |   **1241.93 MB** |        **1.00** |
| &#39;Phase4: QueueFileChangesBatch&#39;      | 1000        |      75.67 ms |    NA | 0.007 |     2000.0000 |            - |            - |     15.96 MB |        0.01 |
|                                      |             |               |       |       |               |              |              |              |             |
| **&#39;Legacy: per-change QueueFileChange&#39;** | **10000**       | **164,971.66 ms** |    **NA** | **1.000** | **14497000.0000** | **6679000.0000** | **5494000.0000** | **108967.94 MB** |       **1.000** |
| &#39;Phase4: QueueFileChangesBatch&#39;      | 10000       |     499.72 ms |    NA | 0.003 |    25000.0000 |    1000.0000 |    1000.0000 |    164.77 MB |       0.002 |
