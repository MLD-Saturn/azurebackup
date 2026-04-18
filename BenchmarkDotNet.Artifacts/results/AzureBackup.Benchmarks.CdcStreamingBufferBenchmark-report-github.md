```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                                         | FileSizeMB | DedupRatio | Mean        | Error | Allocated |
|----------------------------------------------- |----------- |----------- |------------:|------:|----------:|
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **4**          | **0**          |    **35.63 ms** |    **NA** |   **1.07 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **4**          | **1**          |    **34.60 ms** |    **NA** |   **1.07 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **64**         | **0**          |   **524.64 ms** |    **NA** |   **1.12 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **64**         | **1**          |   **517.99 ms** |    **NA** |   **1.14 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **256**        | **0**          | **2,055.30 ms** |    **NA** | **583.24 MB** |
| **&#39;Phase6.1: streaming scratch + per-chunk pool&#39;** | **256**        | **1**          | **2,044.63 ms** |    **NA** |   **1.38 MB** |
