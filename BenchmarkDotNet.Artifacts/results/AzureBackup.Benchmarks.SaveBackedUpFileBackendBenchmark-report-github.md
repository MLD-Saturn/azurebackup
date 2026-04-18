```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-CARYJT : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=5  RunStrategy=Throughput  
UnrollFactor=1  WarmupCount=2  

```
| Method    | Backend | ChunkCount | Mean        | Error      | StdDev    | Allocated |
|---------- |-------- |----------- |------------:|-----------:|----------:|----------:|
| **FirstSave** | **LiteDB**  | **1**          |  **9,082.9 μs** |   **835.9 μs** | **129.35 μs** | **235.95 KB** |
| ReSave    | LiteDB  | 1          |  9,156.8 μs | 3,500.4 μs | 541.69 μs |  220.5 KB |
| **FirstSave** | **LiteDB**  | **10**         |  **9,903.7 μs** | **1,652.9 μs** | **429.25 μs** | **246.61 KB** |
| ReSave    | LiteDB  | 10         |  9,169.4 μs | 1,480.2 μs | 384.40 μs | 242.32 KB |
| **FirstSave** | **LiteDB**  | **100**        | **10,151.1 μs** |   **670.2 μs** | **103.72 μs** | **350.64 KB** |
| ReSave    | LiteDB  | 100        |  9,876.0 μs | 1,537.7 μs | 237.96 μs | 879.55 KB |
| **FirstSave** | **SQLite**  | **1**          |    **657.3 μs** |   **365.9 μs** |  **95.03 μs** |   **9.63 KB** |
| ReSave    | SQLite  | 1          |    555.7 μs |   182.1 μs |  47.28 μs |   9.63 KB |
| **FirstSave** | **SQLite**  | **10**         |    **697.9 μs** |   **231.5 μs** |  **60.13 μs** |  **24.04 KB** |
| ReSave    | SQLite  | 10         |    679.1 μs |   313.4 μs |  81.39 μs |  24.04 KB |
| **FirstSave** | **SQLite**  | **100**        |  **1,914.2 μs** |   **120.3 μs** |  **18.61 μs** | **168.18 KB** |
| ReSave    | SQLite  | 100        |  2,047.5 μs |   304.9 μs |  79.18 μs | 168.18 KB |
