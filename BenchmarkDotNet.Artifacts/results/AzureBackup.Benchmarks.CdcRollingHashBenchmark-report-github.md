```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8655/25H2/2025Update/HudsonValley2) (Hyper-V)
AMD EPYC 7763 2.44GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3


```
| Method                   | Mean      | Error     | StdDev    | Ratio | Allocated | Alloc Ratio |
|------------------------- |----------:|----------:|----------:|------:|----------:|------------:|
| &#39;Legacy: modulo wrap&#39;    | 16.899 ms | 0.0097 ms | 0.0090 ms |  1.00 |      72 B |        1.00 |
| &#39;Phase1: branching wrap&#39; |  6.680 ms | 0.0186 ms | 0.0145 ms |  0.40 |      72 B |        1.00 |
