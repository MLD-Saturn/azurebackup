```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                             | ExistingCollisions | Mean        | Error | Ratio | Allocated | Alloc Ratio |
|----------------------------------- |------------------- |------------:|------:|------:|----------:|------------:|
| **&#39;Legacy: linear ExistsAsync probe&#39;** | **1**                  |    **53.24 ms** |    **NA** |  **1.00** |     **512 B** |        **1.00** |
| &#39;Phase2: single listing call&#39;      | 1                  |    24.97 ms |    NA |  0.47 |     328 B |        0.64 |
|                                    |                    |             |       |       |           |             |
| **&#39;Legacy: linear ExistsAsync probe&#39;** | **10**                 |   **337.94 ms** |    **NA** |  **1.00** |    **2056 B** |        **1.00** |
| &#39;Phase2: single listing call&#39;      | 10                 |    39.46 ms |    NA |  0.12 |     328 B |        0.16 |
|                                    |                    |             |       |       |           |             |
| **&#39;Legacy: linear ExistsAsync probe&#39;** | **50**                 | **1,583.04 ms** |    **NA** |  **1.00** |    **8744 B** |        **1.00** |
| &#39;Phase2: single listing call&#39;      | 50                 |    26.55 ms |    NA |  0.02 |     328 B |        0.04 |
