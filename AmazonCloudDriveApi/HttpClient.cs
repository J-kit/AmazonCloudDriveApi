// <copyright file="HttpClient.cs" company="Rambalac">
// Copyright (c) Rambalac. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Azi.Amazon.CloudDrive.Http
{
    /// <summary>
    /// Http helper class to send REST API requests
    /// </summary>
    internal class HttpClient
    {
        /// <summary>
        /// Maximum number of retries.
        /// </summary>
        public const int RetryTimes = 100;

        private static readonly HashSet<HttpStatusCode> RetryCodes = new HashSet<HttpStatusCode> { HttpStatusCode.ProxyAuthenticationRequired };

        private readonly KeyToValue<int, RetryErrorProcessorDelegate> fileSendRetryErrorProcessorKeyToValue;
        private readonly KeyToValue<int, RetryErrorProcessorDelegate> retryErrorProcessorKeyToValue;
        private readonly Func<HttpWebRequest, Task> settingsSetter;

#if DEBUG
        private long clients;
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpClient"/> class.
        /// Constructs new class with initializing callback.
        /// </summary>
        /// <param name="settingsSetter">Async func to configure HttpWebRequest</param>
        public HttpClient(Func<HttpWebRequest, Task> settingsSetter)
        {
            this.settingsSetter = settingsSetter;
            retryErrorProcessorKeyToValue = new KeyToValue<int, RetryErrorProcessorDelegate>(RetryErrorProcessor);
            fileSendRetryErrorProcessorKeyToValue = new KeyToValue<int, RetryErrorProcessorDelegate>(FileSendRetryErrorProcessor, RetryErrorProcessor);
        }

        /// <summary>
        /// Retry error processor
        /// </summary>
        /// <param name="code">Http code</param>
        /// <returns>Return true if request should be retried. Func reference will be stored as WeakReference, so be careful with anonymous func.</returns>
        public delegate Task<bool> RetryErrorProcessorDelegate(HttpStatusCode code);

        /// <summary>
        /// Gets File Send Http error processors
        /// </summary>
        public Dictionary<int, RetryErrorProcessorDelegate> FileSendRetryErrorProcessor { get; } =
            new Dictionary<int, RetryErrorProcessorDelegate>();

        /// <summary>
        /// Gets general Http error processors
        /// </summary>
        public Dictionary<int, RetryErrorProcessorDelegate> RetryErrorProcessor { get; } =
            new Dictionary<int, RetryErrorProcessorDelegate>();

        private static Encoding Utf8 => new UTF8Encoding(false, true);

        /// <summary>
        /// Returns delay interval depended on number of retries.
        /// </summary>
        /// <param name="time">Number of retry</param>
        /// <returns>Time for delay</returns>
        public static TimeSpan RetryDelay(int time)
        {
            return TimeSpan.FromSeconds(1 << time);
        }

        /// <summary>
        /// Processes exception to decide retry or abort.
        /// </summary>
        /// <param name="ex">Exception to process</param>
        /// <returns>False if retry</returns>
        public async Task<bool> GeneralExceptionProcessor(Exception ex)
        {
            return await ExceptionProcessor(ex, retryErrorProcessorKeyToValue);
        }

        /// <summary>
        /// Returns configured raw HttpWebRequest
        /// </summary>
        /// <param name="url">Request URL</param>
        /// <returns>HttpWebRequest</returns>
        public async Task<HttpWebRequest> GetHttpClient(string url)
        {
            var result = (HttpWebRequest)WebRequest.Create(url);
#if DEBUG
            Debug.WriteLine("Client created: " + clients++);
#endif
            await settingsSetter(result);
            return result;
        }

        /// <summary>
        /// Sends GET request and parses response as JSON
        /// </summary>
        /// <typeparam name="T">type or response</typeparam>
        /// <param name="url">URL for request</param>
        /// <returns>parsed response</returns>
        public async Task<T> GetJsonAsync<T>(string url)
        {
            return await Send<T>(HttpMethod.Get, url);
        }

        /// <summary>
        /// Sends GET request and put response to byte array
        /// </summary>
        /// <param name="url">URL for request</param>
        /// <param name="buffer">Byte array to get data into</param>
        /// <param name="bufferIndex">Offset in buffer</param>
        /// <param name="fileOffset">Offset in file</param>
        /// <param name="length">Number of bytes to download</param>
        /// <returns>Number of bytes downloaded</returns>
        public async Task<int> GetToBufferAsync(string url, byte[] buffer, int bufferIndex, long fileOffset, int length)
        {
            using (var stream = new MemoryStream(buffer, bufferIndex, length))
            {
                await GetToStreamAsync(url, stream, fileOffset, length);
                return (int)stream.Position;
            }
        }

        /// <summary>
        /// Sends GET request and put response to Stream
        /// </summary>
        /// <param name="url">URL for request</param>
        /// <param name="stream">Stream to push result</param>
        /// <param name="fileOffset">Offset in file</param>
        /// <param name="length">Number of bytes to download</param>
        /// <param name="bufferSize">Size of memory buffer for download</param>
        /// <param name="progress">Func for progress. Parameter is current progress, result should be next position after which progress func will be called again</param>
        /// <returns>Async Task</returns>
        public async Task GetToStreamAsync(string url, Stream stream, long? fileOffset = null, long? length = null, int bufferSize = 4096, Func<long, Task<long>> progress = null)
        {
            await GetToStreamAsync(
                url,
                async (response) =>
                    {
                        using (var input = response.GetResponseStream())
                        {
                            Contract.Assert(input != null, "input!=null");
                            var buff = new byte[Math.Min(bufferSize, (response.ContentLength != -1) ? response.ContentLength : long.MaxValue)];
                            int red;
                            long nextProgress = -1;
                            long totalRead = 0;
                            while ((red = await input.ReadAsync(buff, 0, buff.Length)) > 0)
                            {
                                totalRead += red;
                                await stream.WriteAsync(buff, 0, red);
                                if (progress != null && totalRead >= nextProgress)
                                {
                                    nextProgress = await progress.Invoke(totalRead);
                                }
                            }
                            if (nextProgress == -1)
                            {
                                progress?.Invoke(0);
                            }
                        }
                    },
                fileOffset,
                length);
        }

        /// <summary>
        /// Sends GET request and put response to Stream
        /// </summary>
        /// <param name="url">URL for request</param>
        /// <param name="streammer">Async Func to process response</param>
        /// <param name="fileOffset">Offset in file</param>
        /// <param name="length">Number of bytes to download</param>
        /// <returns>Async Task</returns>
        public async Task GetToStreamAsync(string url, Func<HttpWebResponse, Task> streammer, long? fileOffset = null, long? length = null)
        {
            await Retry.Do(
                RetryTimes,
                RetryDelay,
                async () =>
                    {
                        var client = await GetHttpClient(url);
                        if (fileOffset != null && length != null)
                        {
                            client.AddRange((long)fileOffset, (long)(fileOffset + length - 1));
                        }
                        else if (fileOffset != null)
                        {
                            client.AddRange((long)fileOffset);
                        }

                        client.Method = "GET";

                        using (var response = (HttpWebResponse)await client.GetResponseAsync())
                        {
                            if (!response.IsSuccessStatusCode())
                            {
                                return await LogBadResponse(response);
                            }

                            await streammer(response);
                        }
                        return true;
                    },
                GeneralExceptionProcessor);
        }

        /// <summary>
        /// Sends PATCH request with object serialized to JSON and get response as parsed JSON
        /// </summary>
        /// <typeparam name="TParam">Request object type</typeparam>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="url">URL for request</param>
        /// <param name="obj">Object for request</param>
        /// <returns>Async result object</returns>
        public async Task<TResult> Patch<TParam, TResult>(string url, TParam obj)
        {
            return await Send<TParam, TResult>(new HttpMethod("PATCH"), url, obj);
        }

        /// <summary>
        /// Sends POST request with object serialized to JSON and get response as parsed JSON
        /// </summary>
        /// <typeparam name="TParam">Request object type</typeparam>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="url">URL for request</param>
        /// <param name="obj">Object for request</param>
        /// <returns>Async result object</returns>
        public async Task<TResult> Post<TParam, TResult>(string url, TParam obj)
        {
            return await Send<TParam, TResult>(HttpMethod.Post, url, obj);
        }

        /// <summary>
        /// Sends POST request with parameters
        /// </summary>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="url">URL for request</param>
        /// <param name="pars">Post parameters</param>
        /// <returns>Async result object</returns>
        public async Task<TResult> PostForm<TResult>(string url, Dictionary<string, string> pars)
        {
            return await SendForm<TResult>(HttpMethod.Post, url, pars);
        }

        /// <summary>
        /// Sends request with object serialized to JSON and get response as parsed JSON
        /// </summary>
        /// <typeparam name="TParam">Request object type</typeparam>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="method">Http method</param>
        /// <param name="url">URL for request</param>
        /// <param name="obj">Object for request</param>
        /// <returns>Async result object</returns>
        public async Task<TResult> Send<TParam, TResult>(HttpMethod method, string url, TParam obj)
        {
            return await Send(method, url, obj, (r) => r.ReadAsAsync<TResult>());
        }

        /// <summary>
        /// Sends request and get response as parsed JSON
        /// </summary>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="method">HTTP method</param>
        /// <param name="url">URL for request</param>
        /// <returns>Async result object</returns>
        public async Task<TResult> Send<TResult>(HttpMethod method, string url)
        {
            return await Send(method, url, (r) => r.ReadAsAsync<TResult>());
        }

        /// <summary>
        /// Sends request with object serialized to JSON and get response as parsed JSON
        /// </summary>
        /// <typeparam name="TParam">Request object type</typeparam>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="method">HTTP method</param>
        /// <param name="url">URL for request</param>
        /// <param name="obj">Object for request</param>
        /// <param name="responseParser">Func to parse response and return result object</param>
        /// <returns>Async result object</returns>
        public async Task<TResult> Send<TParam, TResult>(HttpMethod method, string url, TParam obj, Func<HttpWebResponse, Task<TResult>> responseParser)
        {
            var result = default(TResult);
            await Retry.Do(
                RetryTimes,
                RetryDelay,
                async () =>
                {
                    var client = await GetHttpClient(url);
                    client.Method = method.ToString();
                    var data = JsonConvert.SerializeObject(obj);
                    using (var content = new StringContent(data))
                    {
                        client.ContentType = content.Headers.ContentType.ToString();

                        using (var output = await client.GetRequestStreamAsync())
                        {
                            await content.CopyToAsync(output);
                        }
                    }

                    using (var response = (HttpWebResponse)await client.GetResponseAsync())
                    {
                        if (!response.IsSuccessStatusCode())
                        {
                            return await LogBadResponse(response);
                        }

                        result = await responseParser(response);
                    }
                    return true;
                },
                GeneralExceptionProcessor);
            return result;
        }

        /// <summary>
        /// Sends request and get response as parsed JSON
        /// </summary>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="method">HTTP method</param>
        /// <param name="url">URL for request</param>
        /// <param name="responseParser">Func to parse response and return result object</param>
        /// <returns>Async result object</returns>
        public async Task<TResult> Send<TResult>(HttpMethod method, string url, Func<HttpWebResponse, Task<TResult>> responseParser)
        {
            var result = default(TResult);
            await Retry.Do(
                RetryTimes,
                RetryDelay,
                async () =>
                    {
                        var client = await GetHttpClient(url);
                        client.Method = method.ToString();

                        using (var response = (HttpWebResponse)await client.GetResponseAsync())
                        {
                            if (!response.IsSuccessStatusCode())
                            {
                                return await LogBadResponse(response);
                            }

                            result = await responseParser(response);
                        }
                        return true;
                    },
                GeneralExceptionProcessor);
            return result;
        }

        /// <summary>
        /// Uploads file
        /// </summary>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="method">HTTP method</param>
        /// <param name="url">URL for request</param>
        /// <param name="fileInfo">File upload parameters. Input stream must support Length</param>
        /// <returns>Async result object</returns>
        public async Task<TResult> SendFile<TResult>(HttpMethod method, string url, SendFileInfo fileInfo)
        {
            var result = default(TResult);
            await Retry.Do(
               RetryTimes,
               RetryDelay,
               async () =>
               {
                   var client = await GetHttpClient(url);
                   try
                   {
                       client.Method = method.ToString();
                       client.AllowWriteStreamBuffering = false;

                       var boundry = Guid.NewGuid().ToString();
                       client.ContentType = $"multipart/form-data; boundary={fileInfo.MultipartBoundary.Boundary}";
                       client.SendChunked = false;

                       using (var input = fileInfo.StreamOpener())
                       {
                           var preFix = fileInfo.MultipartBoundary.GetPrefix(input);
                           var postFix = fileInfo.MultipartBoundary.Postfix;

                           client.ContentLength = preFix.Length + input.Length + postFix.Length;

                           fileInfo.CancellationToken.ThrowIfCancellationRequested();

                           using (var output = await client.GetRequestStreamAsync())
                           {
                               var state = new CopyStreamState();

                               await CopyStreams(preFix, output, fileInfo, null);
                               await CopyStreams(input, output, fileInfo, state);

                               await CopyStreams(postFix, output, fileInfo, null);
                           }
                       }
                       using (var response = (HttpWebResponse)await client.GetResponseAsync())
                       {
                           if (!response.IsSuccessStatusCode())
                           {
                               return await LogBadResponse(response);
                           }

                           result = await response.ReadAsAsync<TResult>();
                       }
                       return true;
                   }
                   catch (Exception)
                   {
                       client.Abort();
                       throw;
                   }
               },
               FileSendExceptionProcessor);
            return result;


        }

        /// <summary>
        /// Sends request with form data
        /// </summary>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="method">HTTP method</param>
        /// <param name="url">URL for request</param>
        /// <param name="pars">Request parameters</param>
        /// <returns>Async result object</returns>
        public async Task<TResult> SendForm<TResult>(HttpMethod method, string url, Dictionary<string, string> pars)
        {
            var result = default(TResult);
            await Retry.Do(
                RetryTimes,
                RetryDelay,
                async () =>
                    {
                        var client = await GetHttpClient(url);
                        client.Method = method.ToString();
                        using (var content = new FormUrlEncodedContent(pars))
                        {
                            client.ContentType = content.Headers.ContentType.ToString();

                            using (var output = await client.GetRequestStreamAsync())
                            {
                                await content.CopyToAsync(output);
                            }
                        }
                        using (var response = (HttpWebResponse)await client.GetResponseAsync())
                        {
                            if (!response.IsSuccessStatusCode())
                            {
                                return await LogBadResponse(response);
                            }

                            result = await response.ReadAsAsync<TResult>();
                        }
                        return true;
                    },
                GeneralExceptionProcessor);
            return result;
        }

        private static async Task CopyStreams(Stream source, Stream destination, SendFileInfo info, CopyStreamState state)
        {
            var buffer = new byte[info.BufferSize];
            int bytesRead;
            var lastProgessCalled = false;
            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, info.CancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead, info.CancellationToken);
                lastProgessCalled = false;
                if (state != null)
                {
                    state.Pos += bytesRead;
                    if (info.Progress != null && (state.Pos >= state.NextPos))
                    {
                        state.NextPos = await info.Progress.Invoke(state.Pos);
                        lastProgessCalled = true;
                    }
                }
            }

            if (state != null && info.Progress != null && !lastProgessCalled)
            {
                await info.Progress.Invoke(state.Pos);
            }
        }

        private static async Task<bool> ExceptionProcessor(Exception ex, KeyToValue<int, RetryErrorProcessorDelegate> retryErrorProcessors)
        {
            if (ex is TaskCanceledException)
            {
                throw ex;
            }

            var webex = SearchForException<WebException>(ex);
            if (webex?.Response is HttpWebResponse webresp)
            {
                if (RetryCodes.Contains(webresp.StatusCode))
                {
                    return false;
                }

                var processor = retryErrorProcessors[(int)webresp.StatusCode];
                if (processor != null)
                {
                    if (await processor(webresp.StatusCode))
                    {
                        return false;
                    }
                }

                using (var str = webresp.GetResponseStream())
                {
                    if (str != null)
                    {
                        var reader = new StreamReader(str);
                        var text = await reader.ReadToEndAsync();
                        throw new HttpWebException(webex.Message, webresp.StatusCode, text);
                    }
                }

                throw new HttpWebException(webex.Message, webresp.StatusCode);
            }

            throw ex;
        }

        private static async Task<bool> LogBadResponse(HttpWebResponse response)
        {
            try
            {
                var message = await response.ReadAsStringAsync();
                if (!RetryCodes.Contains(response.StatusCode))
                {
                    throw new HttpWebException(message, response.StatusCode);
                }

                return false;
            }
            catch (Exception e)
            {
                throw new HttpWebException(e.Message, response.StatusCode, e);
            }
        }

        private static T SearchForException<T>(Exception ex, int depth = 3)
                                                    where T : class
        {
            var cur = ex;
            for (var i = 0; i < depth; i++)
            {
                var res = cur as T;
                if (res != null)
                {
                    return res;
                }

                cur = ex.InnerException;
                if (cur == null)
                {
                    return null;
                }
            }

            return null;
        }

        private async Task<bool> FileSendExceptionProcessor(Exception ex)
        {
            return await ExceptionProcessor(ex, fileSendRetryErrorProcessorKeyToValue);
        }

        private class CopyStreamState
        {
            public long NextPos { get; set; } = -1;

            public long Pos { get; set; }
        }
    }
}