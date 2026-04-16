```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-TOWPHV : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

IterationCount=3  LaunchCount=1  WarmupCount=2  

```
| Method                                | PlaintextSize | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|-------------------------------------- |-------------- |-----------:|------------:|----------:|------:|--------:|---------:|---------:|---------:|----------:|------------:|
| **&#39;Legacy: Decrypt (allocating)&#39;**        | **1024**          |   **1.098 μs** |   **0.2322 μs** | **0.0127 μs** |  **1.00** |    **0.01** |   **0.1717** |        **-** |        **-** |    **1088 B** |        **1.00** |
| &#39;Phase3: DecryptInto (pooled buffer)&#39; | 1024          |   1.038 μs |   0.5764 μs | 0.0316 μs |  0.95 |    0.03 |   0.0057 |        - |        - |      40 B |        0.04 |
|                                       |               |            |             |           |       |         |          |          |          |           |             |
| **&#39;Legacy: Decrypt (allocating)&#39;**        | **65536**         |  **19.100 μs** |   **5.7856 μs** | **0.3171 μs** |  **1.00** |    **0.02** |  **10.4065** |        **-** |        **-** |   **65602 B** |       **1.000** |
| &#39;Phase3: DecryptInto (pooled buffer)&#39; | 65536         |  17.650 μs |   0.5603 μs | 0.0307 μs |  0.92 |    0.01 |        - |        - |        - |      40 B |       0.001 |
|                                       |               |            |             |           |       |         |          |          |          |           |             |
| **&#39;Legacy: Decrypt (allocating)&#39;**        | **1048576**       | **457.124 μs** | **142.8964 μs** | **7.8326 μs** |  **1.00** |    **0.02** | **333.0078** | **333.0078** | **333.0078** | **1048827 B** |       **1.000** |
| &#39;Phase3: DecryptInto (pooled buffer)&#39; | 1048576       | 321.099 μs |  56.7024 μs | 3.1080 μs |  0.70 |    0.01 |        - |        - |        - |      40 B |       0.000 |
