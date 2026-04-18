```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                               | FileSizeMB | DedupRatio | Mean      | Error | Ratio | Allocated | Alloc Ratio |
|------------------------------------- |----------- |----------- |----------:|------:|------:|----------:|------------:|
| **&#39;Phase6: single-pass CDC + dispatch&#39;** | **4**          | **0**          |  **36.60 ms** |    **NA** |  **0.77** |   **1.13 MB** |        **0.56** |
| &#39;Legacy: two-pass CDC + re-read&#39;     | 4          | 0          |  47.42 ms |    NA |  1.00 |   2.01 MB |        1.00 |
|                                      |            |            |           |       |       |           |             |
| **&#39;Phase6: single-pass CDC + dispatch&#39;** | **4**          | **0.5**        |  **35.40 ms** |    **NA** |  **0.83** |   **1.01 MB** |        **0.50** |
| &#39;Legacy: two-pass CDC + re-read&#39;     | 4          | 0.5        |  42.77 ms |    NA |  1.00 |   2.01 MB |        1.00 |
|                                      |            |            |           |       |       |           |             |
| **&#39;Phase6: single-pass CDC + dispatch&#39;** | **4**          | **1**          |  **35.20 ms** |    **NA** |  **0.93** |   **1.01 MB** |        **1.00** |
| &#39;Legacy: two-pass CDC + re-read&#39;     | 4          | 1          |  37.67 ms |    NA |  1.00 |   1.01 MB |        1.00 |
|                                      |            |            |           |       |       |           |             |
| **&#39;Phase6: single-pass CDC + dispatch&#39;** | **64**         | **0**          | **567.94 ms** |    **NA** |  **0.79** |   **3.06 MB** |        **1.48** |
| &#39;Legacy: two-pass CDC + re-read&#39;     | 64         | 0          | 721.89 ms |    NA |  1.00 |   2.07 MB |        1.00 |
|                                      |            |            |           |       |       |           |             |
| **&#39;Phase6: single-pass CDC + dispatch&#39;** | **64**         | **0.5**        | **566.90 ms** |    **NA** |  **0.87** |   **3.07 MB** |        **1.48** |
| &#39;Legacy: two-pass CDC + re-read&#39;     | 64         | 0.5        | 654.30 ms |    NA |  1.00 |   2.07 MB |        1.00 |
|                                      |            |            |           |       |       |           |             |
| **&#39;Phase6: single-pass CDC + dispatch&#39;** | **64**         | **1**          | **558.77 ms** |    **NA** |  **0.99** |   **3.08 MB** |        **1.00** |
| &#39;Legacy: two-pass CDC + re-read&#39;     | 64         | 1          | 566.36 ms |    NA |  1.00 |   3.08 MB |        1.00 |
