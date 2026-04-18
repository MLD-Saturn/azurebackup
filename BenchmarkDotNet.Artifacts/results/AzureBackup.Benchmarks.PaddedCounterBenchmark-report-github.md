```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host] : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Dry    : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                                                    | Threads | Mean      | Error | Ratio | Allocated | Alloc Ratio |
|---------------------------------------------------------- |-------- |----------:|------:|------:|----------:|------------:|
| **&#39;Phase6: PaddedLong (each counter on its own cache line)&#39;** | **2**       |  **38.87 ms** |    **NA** |  **0.27** |     **640 B** |        **1.16** |
| &#39;Legacy: adjacent longs (false-shared cache line)&#39;        | 2       | 144.88 ms |    NA |  1.00 |     552 B |        1.00 |
|                                                           |         |           |       |       |           |             |
| **&#39;Phase6: PaddedLong (each counter on its own cache line)&#39;** | **4**       | **187.45 ms** |    **NA** |  **0.57** |     **992 B** |        **1.10** |
| &#39;Legacy: adjacent longs (false-shared cache line)&#39;        | 4       | 326.21 ms |    NA |  1.00 |     904 B |        1.00 |
|                                                           |         |           |       |       |           |             |
| **&#39;Phase6: PaddedLong (each counter on its own cache line)&#39;** | **8**       | **317.72 ms** |    **NA** |  **0.54** |    **1696 B** |        **1.05** |
| &#39;Legacy: adjacent longs (false-shared cache line)&#39;        | 8       | 587.36 ms |    NA |  1.00 |    1608 B |        1.00 |
