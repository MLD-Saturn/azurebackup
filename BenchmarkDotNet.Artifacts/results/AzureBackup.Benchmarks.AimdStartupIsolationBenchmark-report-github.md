```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8655/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                                                  | Workload            | StartAtCeiling | Mean     | Error     | StdDev   | Gen0       | Gen1       | Gen2      | Allocated |
|-------------------------------------------------------- |-------------------- |--------------- |---------:|----------:|---------:|-----------:|-----------:|----------:|----------:|
| **&#39;End-to-end backup, AIMD start-low vs start-at-ceiling&#39;** | **realistic-large-200** | **False**          | **26.714 s** | **619.414 s** | **1.3760 s** | **26000.0000** | **11000.0000** | **8000.0000** |  **11.14 GB** |
| **&#39;End-to-end backup, AIMD start-low vs start-at-ceiling&#39;** | **realistic-large-200** | **True**           | **17.931 s** | **473.048 s** | **1.0508 s** | **16000.0000** |  **9000.0000** | **6000.0000** |  **11.47 GB** |
| **&#39;End-to-end backup, AIMD start-low vs start-at-ceiling&#39;** | **realistic-large-50**  | **False**          |  **4.055 s** |  **14.036 s** | **0.0312 s** |  **7000.0000** |  **5000.0000** | **5000.0000** |   **2.75 GB** |
| **&#39;End-to-end backup, AIMD start-low vs start-at-ceiling&#39;** | **realistic-large-50**  | **True**           |  **2.032 s** |  **31.855 s** | **0.0708 s** |  **6000.0000** |  **6000.0000** | **6000.0000** |   **2.98 GB** |
