``` ini

BenchmarkDotNet=v0.11.4, OS=Windows 10.0.17763.316 (1809/October2018Update/Redstone5)
Intel Xeon Silver 4108 CPU 1.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET Core SDK=3.0.100-preview-010184
  [Host]     : .NET Core ? (CoreCLR 4.6.27207.03, CoreFX 4.6.27207.03), 64bit RyuJIT
  Job-RPQUAV : .NET Core 2.2.0 (CoreCLR 4.6.27110.04, CoreFX 4.6.27110.04), 64bit RyuJIT

Toolchain=.NET Core 2.2  

```
| Method |     Mean |    Error |   StdDev | Gen 0/1k Op | Gen 1/1k Op | Gen 2/1k Op | Allocated Memory/Op |
|------- |---------:|---------:|---------:|------------:|------------:|------------:|--------------------:|
|   Ping | 123.3 us | 2.510 us | 3.263 us |      0.7324 |           - |           - |             1.05 KB |
