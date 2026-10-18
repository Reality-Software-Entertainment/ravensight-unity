using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace RealitySE
{
    /// <summary>
    /// IRavensightTransport backed by UnityWebRequest. Needs a MonoBehaviour to
    /// drive the coroutine, which the Ravensight singleton supplies.
    ///
    /// Every failure mode is turned into a RavensightResponse. This never
    /// throws and never faults the returned Task, so the retry logic in
    /// RavensightClient sees one uniform outcome type.
    /// </summary>
    public sealed class RavensightUnityTransport : IRavensightTransport
    {
        private readonly MonoBehaviour _runner;
        private readonly int _timeoutSeconds;

        public RavensightUnityTransport(MonoBehaviour runner, int timeoutSeconds)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            _runner = runner;
            _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 15;
        }

        public RavensightUnityTransport(MonoBehaviour runner) : this(runner, 15) { }

        public Task<RavensightResponse> SendAsync(RavensightRequest request)
        {
            TaskCompletionSource<RavensightResponse> completion =
                new TaskCompletionSource<RavensightResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (request == null)
            {
                completion.TrySetResult(RavensightResponse.NetworkFailure("null request"));
                return completion.Task;
            }

            if (_runner == null || !_runner.gameObject.activeInHierarchy)
            {
                // The singleton is gone (scene teardown or application quit).
                completion.TrySetResult(RavensightResponse.NetworkFailure("transport runner unavailable"));
                return completion.Task;
            }

            try
            {
                _runner.StartCoroutine(Run(request, completion));
            }
            catch (Exception e)
            {
                completion.TrySetResult(RavensightResponse.NetworkFailure(e.Message));
            }

            return completion.Task;
        }

        private IEnumerator Run(RavensightRequest request, TaskCompletionSource<RavensightResponse> completion)
        {
            UnityWebRequest www = null;
            UnityWebRequestAsyncOperation sending = null;

            // Construction and SendWebRequest can throw (a bad URL or header,
            // or plain HTTP to a non loopback host when the project disallows
            // it). Do both outside the yield, because C# forbids yield inside a
            // try that has a catch; an exception escaping the coroutine would
            // leave the task, and the client awaiting it, waiting forever.
            try
            {
                www = new UnityWebRequest(request.Url, request.Method);
                if (request.NoRedirect) www.redirectLimit = 0;
                www.downloadHandler = new DownloadHandlerBuffer();

                if (request.RawBody != null || !string.IsNullOrEmpty(request.Body))
                {
                    byte[] payload = request.RawBody ?? Encoding.UTF8.GetBytes(request.Body);
                    UploadHandlerRaw upload = new UploadHandlerRaw(payload);
                    upload.contentType = "application/json";
                    www.uploadHandler = upload;
                }

                foreach (KeyValuePair<string, string> header in request.Headers)
                {
                    www.SetRequestHeader(header.Key, header.Value);
                }

                www.timeout = _timeoutSeconds;
                sending = www.SendWebRequest();
            }
            catch (Exception e)
            {
                if (www != null) www.Dispose();
                completion.TrySetResult(RavensightResponse.NetworkFailure("request setup failed: " + e.Message));
                yield break;
            }

            yield return sending;

            RavensightResponse response;
            try
            {
                int status = (int)www.responseCode;
                if (status <= 0)
                {
                    // Connection refused, DNS failure, timeout, TLS error.
                    response = RavensightResponse.NetworkFailure(www.error ?? "network error");
                }
                else
                {
                    string body = www.downloadHandler != null ? www.downloadHandler.text : null;
                    string retryAfter = www.GetResponseHeader("Retry-After");
                    response = new RavensightResponse(status, body, retryAfter);
                }
            }
            catch (Exception e)
            {
                response = RavensightResponse.NetworkFailure("response read failed: " + e.Message);
            }
            finally
            {
                www.Dispose();
            }

            completion.TrySetResult(response);
        }
    }
}
