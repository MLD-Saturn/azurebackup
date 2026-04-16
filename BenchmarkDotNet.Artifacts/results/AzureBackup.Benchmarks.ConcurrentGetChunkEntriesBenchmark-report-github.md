```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                                     | ConcurrentReaders | Mean       | Error | Ratio | Gen0         | Gen1        | Gen2        | Allocated  | Alloc Ratio |
|------------------------------------------- |------------------ |-----------:|------:|------:|-------------:|------------:|------------:|-----------:|------------:|
| **&#39;Phase5: concurrent reverse-index readers&#39;** | **1**                 |   **380.4 ms** |    **NA** |  **0.82** |   **16000.0000** |  **16000.0000** |  **16000.0000** |   **64.02 MB** |        **0.13** |
| &#39;Legacy: concurrent full-scan readers&#39;     | 1                 |   462.3 ms |    NA |  1.00 |   71000.0000 |  16000.0000 |   1000.0000 |  489.42 MB |        1.00 |
|                                            |                   |            |       |       |              |             |             |            |             |
| **&#39;Phase5: concurrent reverse-index readers&#39;** | **4**                 | **1,625.0 ms** |    **NA** |  **0.89** |   **65000.0000** |  **65000.0000** |  **65000.0000** |   **257.4 MB** |        **0.13** |
| &#39;Legacy: concurrent full-scan readers&#39;     | 4                 | 1,825.2 ms |    NA |  1.00 |  283000.0000 |  66000.0000 |   3000.0000 |  1957.7 MB |        1.00 |
|                                            |                   |            |       |       |              |             |             |            |             |
| **&#39;Phase5: concurrent reverse-index readers&#39;** | **16**                | **6,544.1 ms** |    **NA** |  **0.89** |  **261000.0000** | **261000.0000** | **261000.0000** |  **1029.4 MB** |        **0.13** |
| &#39;Legacy: concurrent full-scan readers&#39;     | 16                | 7,318.6 ms |    NA |  1.00 | 1130000.0000 | 257000.0000 |  26000.0000 | 7830.77 MB |        1.00 |
