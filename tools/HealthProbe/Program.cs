using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
return response.IsSuccessStatusCode ? 0 : 1;
