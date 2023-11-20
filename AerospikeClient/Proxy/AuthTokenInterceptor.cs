/* 
 * Copyright 2012-2023 Aerospike, Inc.
 *
 * Portions may be licensed to Aerospike, Inc. under one or more contributor
 * license agreements.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy of
 * the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations under
 * the License.
 */
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Razor.Runtime.TagHelpers;
using System.Diagnostics;
using System.Text.Json;
using static Aerospike.Client.Log;
using Timer = System.Timers.Timer;

namespace Aerospike.Client
{
    /// <summary>
    /// Interceptor for fetching custom user authorization token
    /// </summary>
    /// <remarks>
    /// For token reauthorization to work properly the driver code would need to be re-factored to allow for Unauthorized exceptions to be retried. 
    /// Also support for OAuth2 tokens or JWT would greatly help instead of this custom token being provided.
    /// </remarks>
    public sealed class AuthTokenInterceptor : Interceptor
    {
        //Do we need a different policy for the proper timeout?
        private ClientPolicy ClientPolicy { get; }
        private GrpcChannel Channel { get; set; }
        
        private AccessToken AccessToken { get; set; }
        private readonly ManualResetEventSlim UpdatingToken = new ManualResetEventSlim(false);
        private Timer RefreshTokenTimer { get; set; }
        

        /*
        public AuthTokenInterceptor(ClientPolicy clientPolicy, GrpcChannel grpcChannel)
        {
            this.ClientPolicy = clientPolicy;
            this.SetChannel(grpcChannel);
        }
        */

        public AuthTokenInterceptor(ClientPolicy clientPolicy)
        {
            this.ClientPolicy = clientPolicy;            
        }

        public void SetChannel(GrpcChannel grpcChannel)
        {
            this.Channel = grpcChannel;

            if (IsTokenRequired())
            {
                if (Log.DebugEnabled())
                    Log.Debug("Grpc Token Required");

                RefreshTokenTimer = new Timer
                {
                    Enabled = false,
                    AutoReset = false,
                };
                RefreshTokenTimer.Elapsed += (sender, e) => RefreshTokenEvent();
                RefreshToken(CancellationToken.None).Wait(this.ClientPolicy.timeout);
            }
        }

        /// <summary>
        /// Called by the Token Refresh Timer <seealso cref="RefreshTokenTimer"/>
        /// Note: <see cref="RefreshToken(CancellationToken)"/> method must be called to activate the timer properly!
        /// </summary>
        /// <remarks>
        /// These are not precise timers and can fire later than the defined interval. 
        /// </remarks>
        private void RefreshTokenEvent()
        {
            //If the Token is not being updated and the token needs to be refreshed, than get a new token... 
            if (this.UpdatingToken.IsSet && this.AccessToken.ShouldRefreshToken)
            {
                if (Log.DebugEnabled())
                    Log.Debug($"Refresh Token Event: Enter: {AccessToken}: '{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")}'");

                if (IsTokenRequired())
                {
                    try
                    {
                        this.RefreshToken(CancellationToken.None).Wait(this.ClientPolicy.timeout);
                    }
                    catch
                    {
                        if (Log.DebugEnabled())
                            Log.Debug($"Refresh Token Event: Exception: {AccessToken}: '{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")}'");
                        throw;
                    }
                }

                if (Log.DebugEnabled())
                    Log.Debug($"Refresh Token Event: Exit: {AccessToken}: '{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")}'");
            }
            else
            {
                if (Log.DebugEnabled())
                    Log.Debug($"Refresh Token Event: Skipped: {AccessToken}: '{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")}'");

            }
        }

        /// <summary>
        /// Performs the act of fetching a new token. 
        /// This will cause all new requests to block until a new token is obtain. 
        /// Request that are already obtained their token and in queue are not effective. 
        /// 
        /// This method also sets/configs the <see cref="RefreshTokenTimer"/> and this must be called after the timer is initially configured.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task RefreshToken(CancellationToken cancellationToken)
        {            
            //Block requests until a new token is obtained...
            this.UpdatingToken.Reset();
            
            //Stop Timer
            RefreshTokenTimer.Stop();
            var prevTimerInterval = this.RefreshTokenTimer.Interval;

            try
            {
               if (Log.DebugEnabled())
                    Log.Debug($"Refresh Token: Enter: {AccessToken}");
                
                var prevToken = this.AccessToken;
                this.AccessToken = await FetchToken(this.Channel,
                                                    this.ClientPolicy.user,
                                                    this.ClientPolicy.password,
                                                    this.ClientPolicy.timeout,
                                                    this.AccessToken,
                                                    cancellationToken);
                RefreshTokenTimer.Interval = AccessToken.RefreshTime;

                //Restart timer
                RefreshTokenTimer.Start();
                prevToken?.Dispose();

                if (Log.DebugEnabled())
                    Log.Debug($"Refresh Token: Exit: {AccessToken}");                
            }
            catch(AerospikeException)
            {
                throw;
            }
            catch (ArgumentException argEx) //thrown if timer interval is bad...
            {
                Log.Error($"Refresh Token Error {AccessToken} Exception: '{argEx}'");
                System.Diagnostics.Debug.WriteLine($"Refresh Token Error {AccessToken} '{argEx}'");

                this.RefreshTokenTimer.Interval = prevTimerInterval;
                //Restart timer
                RefreshTokenTimer.Start();
            }
            catch (OperationCanceledException)
            {
                Log.Error($"Refresh Token: Cancellation: {AccessToken}: '{DateTime.UtcNow}'");
                throw;
            }            
            catch (Exception ex)
            {                
                Log.Error($"Refresh Token Error {AccessToken} Exception: '{ex}'");
                System.Diagnostics.Debug.WriteLine($"Refresh Token Error {AccessToken} '{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")}' '{ex}'");

                throw;
            }
            finally
            {                
                //Always Unblock requests
                this.UpdatingToken.Set();
            }
        }

        /// <summary>
        /// Fetch the new token regardless of TTL...
        /// Should not be called directly...
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="userName"></param>
        /// <param name="password"></param>
        /// <param name="timeout"></param>
        /// <param name="currentToken"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<AccessToken> FetchToken(GrpcChannel channel,
                                                            string userName,
                                                            string password,
                                                            long timeout,
                                                            AccessToken currentToken,
                                                            CancellationToken cancellationToken)
        {           
            try
            {
                if (Log.DebugEnabled())
                    Log.Debug($"FetchToken: Enter: {currentToken}");

                //This will not be the true Get latency since it may include scheduling costs
                var trackLatency = Stopwatch.StartNew();
                var authRequest = new Auth.AerospikeAuthRequest
                {
                    Username = userName,
                    Password = password
                };

                var client = new Auth.AuthService.AuthServiceClient(channel);
                var response = await client.GetAsync(authRequest,
                                                        cancellationToken: cancellationToken,
                                                        deadline: DateTime.UtcNow.AddMilliseconds(timeout));                                    
                trackLatency.Stop();
                
                if (Log.DebugEnabled())
                    Log.Debug($"FetchToken: Server Responded: Latency: {trackLatency.ElapsedMilliseconds}");

                var newToken = ParseToken(response.Token, 
                                            trackLatency.ElapsedMilliseconds,
                                            timeout);
                
                if (Log.DebugEnabled())
                    Log.Debug($"FetchToken: Exchanged New Token: {newToken}");

                return newToken;
            }
            catch (OperationCanceledException)
            {
                Log.Error($"FetchToken: Cancellation: {currentToken}: '{DateTime.UtcNow}'");
                throw;
            }
            catch (RpcException e)
            {
                Log.Error($"FetchToken: Error: {currentToken}: '{DateTime.UtcNow}': Exception: '{e}'");
                System.Diagnostics.Debug.WriteLine($"FetchToken: Error: {currentToken}: '{DateTime.UtcNow}': '{e}'");
                
                throw GRPCConversions.ToAerospikeException(e, (int) timeout, false);
            }            
        }
        
        private static AccessToken ParseToken(string token, long tokenFetchLatency, long timeout)
        {
            string claims = token.Split(".")[1];
            claims = claims.Replace('_', '/').Replace('-', '+');
            int extraEquals = claims.Length % 4;
            if (extraEquals != 0)
            {
                for (int i = 0; i < 4 - extraEquals; i++)
                {
                    claims += "=";
                }
            }
            byte[] decodedClaims = Convert.FromBase64String(claims);
            var strClaims = System.Text.Encoding.UTF8.GetString(decodedClaims.ToArray());
            Dictionary<string, object> parsedClaims = (Dictionary<string, object>)System.Text.Json.JsonSerializer.Deserialize(strClaims, typeof(Dictionary<string, object>));
            JsonElement expiryToken = (JsonElement)parsedClaims.GetValueOrDefault("exp");
            JsonElement iat = (JsonElement)parsedClaims.GetValueOrDefault("iat");
            if (expiryToken.ValueKind == JsonValueKind.Number && iat.ValueKind == JsonValueKind.Number)
            {
                long expiryTokenLong = expiryToken.GetInt64();
                long iatLong = iat.GetInt64();
                long ttl = (expiryTokenLong - iatLong) * 1000;
                if (ttl <= 0)
                {
                    Log.Error($"ParseToken Error 'iat' > 'exp' token: '{strClaims}'");
                    System.Diagnostics.Debug.WriteLine($"ParseToken 'iat' > 'exp' Error token: '{strClaims}'");

                    throw new AerospikeException("token 'iat' > 'exp'");
                }

                return new AccessToken(ttl, token, tokenFetchLatency, timeout);
            }
            else
            {
                Log.Error($"ParseToken Error token: '{strClaims}'");
                System.Diagnostics.Debug.WriteLine($"ParseToken Error token: '{strClaims}'");
                
                throw new AerospikeException("Unsupported access token format");
            }
        }

        /// <summary>
        /// Determines if a Token is required
        /// </summary>
        /// <returns>True id token is required</returns>
        public bool IsTokenRequired()
        {
            return ClientPolicy.user != null;
        }
        
        /// <summary>
        /// Returns a token is one is required. 
        /// If the token is being updated, the call is blocked waiting for the token to be reauthorized.
        /// If the token is current, it is returned.
        /// </summary>
        /// <returns>returns current token or null indicating a token is not required</returns>
        /// <param name="cancellationToken"></param>
        public async Task<AccessToken> GetToken(CancellationToken cancellationToken)
        {            
            if (IsTokenRequired())
            {
                if (AccessToken.HasExpired && this.UpdatingToken.IsSet)
                {
                    this.UpdatingToken.Reset();
                    if (Log.DebugEnabled())
                        Log.Debug($"GetTokenIfNeeded: Expired: Token: {AccessToken}");

                    await this.RefreshToken(cancellationToken);

                    if (Log.DebugEnabled())
                        Log.Debug($"GetTokenIfNeeded: New Token: {AccessToken}");
                }

                //If token being updated, the request will be blocked until the new token is obtained.
                this.UpdatingToken.Wait(this.ClientPolicy.timeout, cancellationToken);

                return this.AccessToken;
            }

            return null;
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
        {
                        
            if (Log.DebugEnabled())
                Log.Debug($"BlockingUnaryCall<TRequest, TResponse>: Enter: {AccessToken}");

            return continuation(request, context);            
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
           
            if (Log.DebugEnabled())
                Log.Debug($"AsyncUnaryCall<TRequest, TResponse>: Enter: {AccessToken}");

            var call = continuation(request, context);
            return new AsyncUnaryCall<TResponse>(HandleResponse(call.ResponseAsync), call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);        
        }

        private async Task<TResponse> HandleResponse<TResponse>(Task<TResponse> t)
        {
            var response = await t;
            return response;
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
        {
           
            if (Log.DebugEnabled())
                Log.Debug($"AsyncClientStreamingCall<TRequest, TResponse>: Enter: {AccessToken}");

            return continuation(context);            
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
        {            
            if (Log.DebugEnabled())
                Log.Debug($"AsyncServerStreamingCall<TRequest, TResponse>: Enter: {AccessToken}");

            return continuation(request, context);            
        }

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
        {           
            if (Log.DebugEnabled())
                Log.Debug($"AsyncDuplexStreamingCall<TRequest, TResponse>: Enter: {AccessToken}");

            return continuation(context);            
        }
    }

    [DebuggerDisplay("{ToString(),nq}")]
    public sealed class AccessToken : IDisposable
    {
        /// <summary>
        /// This factor is used when the <see cref="RefreshTime"/> calculation is less or equal to zero
        /// </summary>
        private const float refreshZeroFraction = 0.50f;
        /// <summary>
        /// This factor is used when the <see cref="RefreshTime"/> calculation is greater or equal to <see cref="ttl"/>
        /// </summary>
        private const float refreshAfterFraction = 0.75f;

        /// <summary>
        /// Local token expiry timestamp in mills.
        /// </summary>
        private readonly Stopwatch expiry;
        /// <summary>
        /// Remaining time to live for the token in mills.
        /// </summary>
        private readonly long ttl;

        /// <summary>
        /// The time before <see cref="ttl"/> to obtain the new token by getting a new one from the proxy server.
        /// This field is calculated from <see cref="ttl"/> taking into consideration network latency and other factors like timer delay, etc.
        /// </summary>
        public readonly long RefreshTime;

        /// <summary>
        /// An access token for Aerospike proxy.
        /// </summary>
        public readonly string Token;

        /// <summary>
        /// The latency involved when fetching the token. 
        /// </summary>
        public readonly long TokenFetchLatency;
        
        public AccessToken(long ttl, string token, long tokenFetchLatency, long timeout)
        {
            this.expiry = Stopwatch.StartNew();
            this.ttl = ttl;
            this.RefreshTime = ttl - Math.Min((long)(ttl* refreshAfterFraction)-tokenFetchLatency,timeout);
            if(this.RefreshTime <= 0)
            {
                this.RefreshTime = (long)Math.Floor(ttl * refreshZeroFraction);
            }
            else if(this.RefreshTime >= ttl)
            {
                this.RefreshTime = (long)Math.Floor(ttl * refreshAfterFraction);
            }
            this.TokenFetchLatency = tokenFetchLatency;
            this.Token = token;

            if (Log.DebugEnabled())
                System.Diagnostics.Debug.WriteLine(this);
        }

        /// <summary>
        /// Token Has Expired
        /// </summary>
        public bool HasExpired => expiry.ElapsedMilliseconds >= ttl;

        /// <summary>
        /// Token should be refreshed before hitting expiration
        /// </summary>
        public bool ShouldRefreshToken => expiry.ElapsedMilliseconds >= RefreshTime;

        public override string ToString()
        {
            var expired = this.HasExpired ? ", Expired:true" : (this.ShouldRefreshToken ? ", NeedRefresh:true" : string.Empty);
            var disposed = this.Disposed ? ", Disposed:true" : string.Empty;

            return $"AccessToken{{TokenHash:{Token?.GetHashCode()}, RunningTime:{expiry.ElapsedMilliseconds}, TTL:{ttl}, RefreshOn:{RefreshTime}, Latency:{TokenFetchLatency}{expired}{disposed}}}";
        }

        public bool Disposed { get; private set; }

        private void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                if (disposing)
                {
                    this.expiry.Stop();                    
                }

                Disposed = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}

