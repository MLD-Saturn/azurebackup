```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                               | FilesToDelete | Mean     | Error | Ratio | Gen0         | Gen1         | Gen2         | Allocated   | Alloc Ratio |
|------------------------------------- |-------------- |---------:|------:|------:|-------------:|-------------:|-------------:|------------:|------------:|
| **&#39;Phase5: bulk reverse-index lookups&#39;** | **10**            |  **3.813 s** |    **NA** |  **0.85** |  **160000.0000** |  **160000.0000** |  **160000.0000** |   **642.52 MB** |        **0.13** |
| &#39;Legacy: bulk full-scan lookups&#39;     | 10            |  4.483 s |    NA |  1.00 |  706000.0000 |  165000.0000 |   16000.0000 |  4894.23 MB |        1.00 |
|                                      |               |          |       |       |              |              |              |             |             |
| **&#39;Phase5: bulk reverse-index lookups&#39;** | **100**           | **32.977 s** |    **NA** |  **0.73** | **1600000.0000** | **1600000.0000** | **1600000.0000** |  **6430.42 MB** |        **0.13** |
| &#39;Legacy: bulk full-scan lookups&#39;     | 100           | 44.949 s |    NA |  1.00 | 7054000.0000 | 1595000.0000 |  152000.0000 | 48942.24 MB |        1.00 |
