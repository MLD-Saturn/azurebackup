```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8655/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  Job-VWSJFF : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=2  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=1  

```
| Method                                       | Workload             | Mean        | Error        | StdDev      | Gen0       | Gen1      | Gen2      | Allocated   |
|--------------------------------------------- |--------------------- |------------:|-------------:|------------:|-----------:|----------:|----------:|------------:|
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **large-skew-100**       |  **3,122.7 ms** |  **33,302.2 ms** |    **73.98 ms** |  **5000.0000** | **5000.0000** | **5000.0000** |  **2055.38 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **large-skew-200**       |  **3,482.6 ms** |  **65,472.2 ms** |   **145.44 ms** |  **6000.0000** | **5000.0000** | **5000.0000** |  **3292.22 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **mixed-realistic-100**  |  **1,212.7 ms** |  **42,553.2 ms** |    **94.53 ms** |  **4000.0000** | **4000.0000** | **4000.0000** |   **379.74 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **mixed-realistic-1000** |  **8,151.9 ms** |  **34,378.6 ms** |    **76.37 ms** | **10000.0000** | **6000.0000** | **6000.0000** |   **7058.2 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **realistic-large-200**  | **26,798.7 ms** | **636,574.9 ms** | **1,414.11 ms** | **26000.0000** | **9000.0000** | **5000.0000** | **11385.44 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **realistic-large-50**   |  **4,107.0 ms** |  **10,853.0 ms** |    **24.11 ms** |  **8000.0000** | **5000.0000** | **5000.0000** |  **2816.17 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **uniform-1MB-100**      |    **247.9 ms** |   **5,706.1 ms** |    **12.68 ms** |  **4000.0000** | **4000.0000** | **4000.0000** |   **302.98 MB** |
| **&#39;End-to-end backup, unmodified orchestrator&#39;** | **uniform-1MB-1000**     |  **2,635.6 ms** |  **17,354.2 ms** |    **38.55 ms** |  **7000.0000** | **7000.0000** | **7000.0000** |  **2376.37 MB** |
