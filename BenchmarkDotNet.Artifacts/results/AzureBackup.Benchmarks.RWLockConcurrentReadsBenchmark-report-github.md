```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                                    | Readers | Mean      | Error | Gen0       | Gen1       | Gen2       | Allocated |
|------------------------------------------ |-------- |----------:|------:|-----------:|-----------:|-----------:|----------:|
| **&#39;Phase5: N parallel readers under RWLock&#39;** | **1**       |  **37.12 ms** |    **NA** |          **-** |          **-** |          **-** |   **7.98 MB** |
| **&#39;Phase5: N parallel readers under RWLock&#39;** | **4**       | **106.24 ms** |    **NA** |  **3000.0000** |  **2000.0000** |  **1000.0000** |  **31.95 MB** |
| **&#39;Phase5: N parallel readers under RWLock&#39;** | **16**      | **352.25 ms** |    **NA** | **14000.0000** | **10000.0000** |  **6000.0000** | **127.64 MB** |
| **&#39;Phase5: N parallel readers under RWLock&#39;** | **32**      | **538.28 ms** |    **NA** | **31000.0000** | **18000.0000** | **12000.0000** | **255.24 MB** |
