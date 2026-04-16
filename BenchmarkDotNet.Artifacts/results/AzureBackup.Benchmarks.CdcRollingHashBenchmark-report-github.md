```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-9700K CPU 3.60GHz (Coffee Lake), 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]     : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3
  Job-WRIKEU : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v3

IterationCount=5  LaunchCount=1  WarmupCount=2  

```
| Method                   | Mean      | Error     | StdDev    | Ratio | Allocated | Alloc Ratio |
|------------------------- |----------:|----------:|----------:|------:|----------:|------------:|
| &#39;Legacy: modulo wrap&#39;    | 11.530 ms | 0.0409 ms | 0.0063 ms |  1.00 |      72 B |        1.00 |
| &#39;Phase1: branching wrap&#39; |  8.457 ms | 0.1983 ms | 0.0307 ms |  0.73 |      72 B |        1.00 |
