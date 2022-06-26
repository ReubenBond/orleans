using System.Net;
using Microsoft.Extensions.Configuration;

namespace Orleans.Networking.Transport;

public class EndpointInfo
{
    public string EndpointName { get; set; }
    public string TransportName { get; set; }
    public EndPoint Endpoint { get; set; }
    public IConfiguration Configuration { get; set; }
}

/*
// Remote endpoint, from membership table

IConfiguration:
{
    "silo": {
      "is_proxy": false,
      "transport": "tcp",
      "tls": {
        "certificate_thumbprint": "xxxxx"
       },
    },
    "client": {
      "is_proxy": true,
      "transport": "tcp",
      "tls": {
        "certificate_thumbprint": "xxxxx"
       },
    },
    "geo": {
       "is_proxy": true,
       "transport": "https",
       "tls": {
         "certificate_thumbprint": "xxxxx"
       }
    }
   
]
*/
