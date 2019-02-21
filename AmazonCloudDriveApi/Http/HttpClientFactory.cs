using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace Azi.Amazon.CloudDrive.Http
{
    internal class HttpClientFactory
    {
        private HttpClientSettings _settings;

        public HttpClientFactory()
        {
            _settings = new HttpClientSettings(null);
        }

        public HttpClient MakeClient(string url)
        {
            return new HttpClient(new NewHttpHandler(_settings));
            //if (url.StartsWith("https://content-"))
            //{
            //    return new HttpClient(new NewHttpHandler(_settings));
            //}

            //return new HttpClient();
        }

        public void Return(HttpClient client)
        {
            //Do smth
        }
    }

    public class HttpClientSettings
    {
        private Func<Task<string>> _tokenGetter;

        public HttpClientSettings(Func<Task<string>> tokenGetter)
        {
            _tokenGetter = tokenGetter;
        }

        public async Task ApplySettings(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = new Version("2.0");

            request.Headers.Remove("UserAgent");
            request.Headers.Add("UserAgent", "AZIACDDokanNet/" + GetType().Assembly.ImageRuntimeVersion);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokenGetter());

            request.Headers.CacheControl.NoCache = true;
            request.Headers.TransferEncodingChunked = true;
        }
    }

    public class NewHttpHandler : WinHttpHandler
    {
        private readonly HttpClientSettings _settings;

        public NewHttpHandler(HttpClientSettings settings)
        {
            AutomaticDecompression = DecompressionMethods.GZip;//| DecompressionMethods.Deflate
            this._settings = settings;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _settings.ApplySettings(request, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}