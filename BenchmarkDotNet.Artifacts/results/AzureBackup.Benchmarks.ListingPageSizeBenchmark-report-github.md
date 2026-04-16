```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                               | PageSize | Mean       | Error | Allocated |
|------------------------------------- |--------- |-----------:|------:|----------:|
| **&#39;Simulated GetBlobsAsync pagination&#39;** | **1000**     | **3,116.0 ms** |    **NA** |  **16.58 KB** |
| **&#39;Simulated GetBlobsAsync pagination&#39;** | **5000**     |   **621.7 ms** |    **NA** |   **3.48 KB** |
| **&#39;Simulated GetBlobsAsync pagination&#39;** | **10000**    |   **317.3 ms** |    **NA** |   **1.81 KB** |
