``` ini

BenchmarkDotNet=v0.11.3, OS=Windows 10.0.17763.253 (1809/October2018Update/Redstone5)
Intel Xeon Silver 4108 CPU 1.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET Core SDK=2.2.101
  [Host]     : .NET Core 2.2.0 (CoreCLR 4.6.27110.04, CoreFX 4.6.27110.04), 64bit RyuJIT
  DefaultJob : .NET Core 2.2.0 (CoreCLR 4.6.27110.04, CoreFX 4.6.27110.04), 64bit RyuJIT


```
|  Method |     Mean |    Error |    StdDev | Gen 0/1k Op | Gen 1/1k Op | Gen 2/1k Op | Allocated Memory/Op |
|-------- |---------:|---------:|----------:|------------:|------------:|------------:|--------------------:|
|    Echo | 211.0 us | 4.365 us | 12.455 us |      2.4414 |           - |           - |              1328 B |
| NewEcho | 169.3 us | 2.594 us |  2.426 us |      2.1973 |           - |           - |               856 B |
