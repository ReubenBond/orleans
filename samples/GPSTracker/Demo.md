docker run -p 9090:9090 -v c:\dev\orleans3\samples\GPSTracker\prometheus.yml:/etc/prometheus/prometheus.yml prom/prometheus



docker run -d --name jaeger -e COLLECTOR_ZIPKIN_HOST_PORT=:9411 -p 5775:5775/udp -p 6831:6831/udp -p 6832:6832/udp -p 5778:5778 -p 16686:16686 -p 14268:14268 -p 14250:14250 -p 9411:9411 jaegertracing/all-in-one:1.22

OR zipkin:
docker run -p 9411:9411 openzipkin/zipkin