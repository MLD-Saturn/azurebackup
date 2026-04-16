```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                                   | TotalChunks | Mean        | Error | Gen0         | Gen1        | Gen2       | Allocated   |
|----------------------------------------- |------------ |------------:|------:|-------------:|------------:|-----------:|------------:|
| **&#39;Phase5: one-time reverse-index rebuild&#39;** | **10000**       |    **749.9 ms** |    **NA** |   **56000.0000** |   **5000.0000** |  **3000.0000** |   **447.28 MB** |
| **&#39;Phase5: one-time reverse-index rebuild&#39;** | **100000**      |  **5,592.6 ms** |    **NA** |  **725000.0000** |  **25000.0000** | **11000.0000** |  **5397.37 MB** |
| **&#39;Phase5: one-time reverse-index rebuild&#39;** | **500000**      | **47,010.0 ms** |    **NA** | **4336000.0000** | **281000.0000** | **34000.0000** | **35002.02 MB** |
