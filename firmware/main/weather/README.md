# Weather service

The weather worker requests current conditions, 25 hourly entries and five daily
forecasts from Open-Meteo over HTTPS with certificate and hostname validation.
Settings supply location; no personal default or pairing secret is sent.
The UI displays Now, 24 hours and 5 days, plus compact Home/Clock summaries.

The RAM cache survives request failures, marks older results stale and is lost
on reboot. Forecast changes do not reset idle or night-screen policies. Celsius
is the source unit; optional Fahrenheit conversion is applied only while drawing.
Sunny, cloudy, rainy and cloudless-night conditions use their matching crab sheets;
unmatched conditions use idle. The UI thread does not perform network requests.

Attribution and transformation notices are in the display About page, companion
About and THIRD_PARTY_NOTICES.md. Data: https://open-meteo.com/, CC BY 4.0;
geocoding: GeoNames via Open-Meteo. Hosted API terms apply separately.
Authenticated /health exposes cache availability and freshness for diagnostics.
