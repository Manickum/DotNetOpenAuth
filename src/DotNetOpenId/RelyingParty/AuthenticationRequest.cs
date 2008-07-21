using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Web;

namespace DotNetOpenId.RelyingParty {
	/// <summary>
	/// Indicates the mode the Provider should use while authenticating the end user.
	/// </summary>
	public enum AuthenticationRequestMode {
		/// <summary>
		/// The Provider should use whatever credentials are immediately available
		/// to determine whether the end user owns the Identifier.  If sufficient
		/// credentials (i.e. cookies) are not immediately available, the Provider
		/// should fail rather than prompt the user.
		/// </summary>
		Immediate,
		/// <summary>
		/// The Provider should determine whether the end user owns the Identifier,
		/// displaying a web page to the user to login etc., if necessary.
		/// </summary>
		Setup
	}

	[DebuggerDisplay("ClaimedIdentifier: {ClaimedIdentifier}, Mode: {Mode}, OpenId: {protocol.Version}")]
	class AuthenticationRequest : IAuthenticationRequest {
		Association assoc;
		ServiceEndpoint endpoint;
		MessageEncoder encoder;
		Protocol protocol { get { return endpoint.Protocol; } }

		AuthenticationRequest(string token, Association assoc, ServiceEndpoint endpoint,
			Realm realm, Uri returnToUrl, MessageEncoder encoder) {
			if (endpoint == null) throw new ArgumentNullException("endpoint");
			if (realm == null) throw new ArgumentNullException("realm");
			if (returnToUrl == null) throw new ArgumentNullException("returnToUrl");
			if (encoder == null) throw new ArgumentNullException("encoder");
			this.assoc = assoc;
			this.endpoint = endpoint;
			this.encoder = encoder;
			Realm = realm;
			ReturnToUrl = returnToUrl;

			Mode = AuthenticationRequestMode.Setup;
			OutgoingExtensions = ExtensionArgumentsManager.CreateOutgoingExtensions(endpoint.Protocol);
			ReturnToArgs = new Dictionary<string, string>();
			if (token != null)
				AddCallbackArguments(DotNetOpenId.RelyingParty.Token.TokenKey, token);
		}
		internal static AuthenticationRequest Create(Identifier userSuppliedIdentifier,
			OpenIdRelyingParty relyingParty, Realm realm, Uri returnToUrl, IRelyingPartyApplicationStore store) {
			if (userSuppliedIdentifier == null) throw new ArgumentNullException("userSuppliedIdentifier");
			if (relyingParty == null) throw new ArgumentNullException("relyingParty");
			if (realm == null) throw new ArgumentNullException("realm");

			userSuppliedIdentifier = userSuppliedIdentifier.TrimFragment();
			Logger.InfoFormat("Creating authentication request for user supplied Identifier: {0}",
				userSuppliedIdentifier);
			Logger.DebugFormat("Realm: {0}", realm);
			Logger.DebugFormat("Return To: {0}", returnToUrl);

			if (Logger.IsWarnEnabled && returnToUrl.Query != null) {
				NameValueCollection returnToArgs = HttpUtility.ParseQueryString(returnToUrl.Query);
				foreach (string key in returnToArgs) {
					if (OpenIdRelyingParty.ShouldParameterBeStrippedFromReturnToUrl(key)) {
						Logger.WarnFormat("OpenId argument \"{0}\" found in return_to URL.  This can corrupt an OpenID response.", key);
						break;
					}
				}
			}

			var endpoints = new List<ServiceEndpoint>(userSuppliedIdentifier.Discover());
			ServiceEndpoint endpoint = selectEndpoint(endpoints.AsReadOnly(), relyingParty, store);
			if (endpoint == null)
				throw new OpenIdException(Strings.OpenIdEndpointNotFound);
			Logger.DebugFormat("Discovered provider endpoint: {0}", endpoint);

			// Throw an exception now if the realm and the return_to URLs don't match
			// as required by the provider.  We could wait for the provider to test this and
			// fail, but this will be faster and give us a better error message.
			if (!realm.Contains(returnToUrl))
				throw new OpenIdException(string.Format(CultureInfo.CurrentCulture,
					Strings.ReturnToNotUnderRealm, returnToUrl, realm));

			string token = new Token(endpoint).Serialize(store);
			// Retrieve the association, but don't create one, as a creation was already
			// attempted by the selectEndpoint method.
			Association association = store != null ? getAssociation(endpoint, store, false) : null;

			return new AuthenticationRequest(
				token, association, endpoint, realm, returnToUrl, relyingParty.Encoder);
		}

		/// <summary>
		/// Returns a filtered and sorted list of the available OP endpoints for a discovered Identifier.
		/// </summary>
		private static List<ServiceEndpoint> filterAndSortEndpoints(ReadOnlyCollection<ServiceEndpoint> endpoints,
			OpenIdRelyingParty relyingParty) {
			if (endpoints == null) throw new ArgumentNullException("endpoints");
			if (relyingParty == null) throw new ArgumentNullException("relyingParty");

			// Filter the endpoints based on criteria given by the host web site.
			List<IXrdsProviderEndpoint> filteredEndpoints = new List<IXrdsProviderEndpoint>(endpoints.Count);
			var filter = relyingParty.EndpointFilter;
			foreach (ServiceEndpoint endpoint in endpoints) {
				if (filter == null || filter(endpoint)) {
					filteredEndpoints.Add(endpoint);
				}
			}

			// Sort endpoints so that the first one in the list is the most preferred one.
			filteredEndpoints.Sort(relyingParty.EndpointOrder);

			List<ServiceEndpoint> endpointList = new List<ServiceEndpoint>(filteredEndpoints.Count);
			foreach (ServiceEndpoint endpoint in filteredEndpoints) {
				endpointList.Add(endpoint);
			}
			return endpointList;
		}

		/// <summary>
		/// Chooses which provider endpoint is the best one to use.
		/// </summary>
		/// <returns>The best endpoint, or null if no acceptable endpoints were found.</returns>
		private static ServiceEndpoint selectEndpoint(ReadOnlyCollection<ServiceEndpoint> endpoints, 
			OpenIdRelyingParty relyingParty, IRelyingPartyApplicationStore store) {

			List<ServiceEndpoint> filteredEndpoints = filterAndSortEndpoints(endpoints, relyingParty);

			// If there are no endpoint candidates...
			if (filteredEndpoints.Count == 0) {
				return null;
			}

			// If we don't have an application store, we have no place to record an association to
			// and therefore can only take our best shot at one of the endpoints.
			if (store == null) {
				return filteredEndpoints[0];
			}

			// Go through each endpoint until we find one that we can successfully create
			// an association with.  This is our only hint about whether an OP is up and running.
			// The idea here is that we don't want to redirect the user to a dead OP for authentication.
			// If the user has multiple OPs listed in his/her XRDS document, then we'll go down the list
			// and try each one until we find one that's good.
			foreach (ServiceEndpoint endpointCandidate in filteredEndpoints) {
				// One weakness of this method is that an OP that's down, but with whom we already
				// created an association in the past will still pass this "are you alive?" test.
				Association association = getAssociation(endpointCandidate, store, true);
				if (association != null) {
					// We have a winner!
					return endpointCandidate;
				}
			}

			// Since all OPs failed to form an association with us, just return the first endpoint
			// and hope for the best.
			return endpoints[0];
		}
		static Association getAssociation(ServiceEndpoint provider, IRelyingPartyApplicationStore store, bool createNewAssociationIfNeeded) {
			if (provider == null) throw new ArgumentNullException("provider");
			if (store == null) throw new ArgumentNullException("store");
			Association assoc = store.GetAssociation(provider.ProviderEndpoint);

			if ((assoc == null || !assoc.HasUsefulLifeRemaining) && createNewAssociationIfNeeded) {
				var req = AssociateRequest.Create(provider);
				if (req.Response != null) {
					// try again if we failed the first time and have a worthy second-try.
					if (req.Response.Association == null && req.Response.SecondAttempt != null) {
						Logger.Warn("Initial association attempt failed, but will retry with Provider-suggested parameters.");
						req = req.Response.SecondAttempt;
					}
					assoc = req.Response.Association;
					if (assoc != null) {
						Logger.InfoFormat("Association with {0} established.", provider.ProviderEndpoint);
						store.StoreAssociation(provider.ProviderEndpoint, assoc);
					} else {
						Logger.ErrorFormat("Association attempt with {0} provider failed.", provider);
					}
				}
			}

			return assoc;
		}

		/// <summary>
		/// Extension arguments to pass to the Provider.
		/// </summary>
		protected ExtensionArgumentsManager OutgoingExtensions { get; private set; }
		/// <summary>
		/// Arguments to add to the return_to part of the query string, so that
		/// these values come back to the consumer when the user agent returns.
		/// </summary>
		protected IDictionary<string, string> ReturnToArgs { get; private set; }

		public AuthenticationRequestMode Mode { get; set; }
		public Realm Realm { get; private set; }
		public Uri ReturnToUrl { get; private set; }
		public Identifier ClaimedIdentifier {
			get { return IsDirectedIdentity ? null : endpoint.ClaimedIdentifier; }
		}
		public bool IsDirectedIdentity {
			get { return endpoint.ClaimedIdentifier == endpoint.Protocol.ClaimedIdentifierForOPIdentifier; }
		}
		/// <summary>
		/// The detected version of OpenID implemented by the Provider.
		/// </summary>
		public Version ProviderVersion { get { return protocol.Version; } }
		/// <summary>
		/// Gets information about the OpenId Provider, as advertised by the
		/// OpenId discovery documents found at the <see cref="ClaimedIdentifier"/>
		/// location.
		/// </summary>
		IProviderEndpoint IAuthenticationRequest.Provider { get { return endpoint; } }
		/// <summary>
		/// Gets the URL the user agent should be redirected to to begin the 
		/// OpenID authentication process.
		/// </summary>
		public IResponse RedirectingResponse {
			get {
				UriBuilder returnToBuilder = new UriBuilder(ReturnToUrl);
				UriUtil.AppendQueryArgs(returnToBuilder, this.ReturnToArgs);

				var qsArgs = new Dictionary<string, string>();

				qsArgs.Add(protocol.openid.mode, (Mode == AuthenticationRequestMode.Immediate) ?
					protocol.Args.Mode.checkid_immediate : protocol.Args.Mode.checkid_setup);
				qsArgs.Add(protocol.openid.identity, endpoint.ProviderLocalIdentifier);
				if (endpoint.Protocol.QueryDeclaredNamespaceVersion != null)
					qsArgs.Add(protocol.openid.ns, endpoint.Protocol.QueryDeclaredNamespaceVersion);
				if (endpoint.Protocol.Version.Major >= 2) {
					qsArgs.Add(protocol.openid.claimed_id, endpoint.ClaimedIdentifier);
				}
				qsArgs.Add(protocol.openid.Realm, Realm);
				qsArgs.Add(protocol.openid.return_to, returnToBuilder.Uri.AbsoluteUri);

				if (this.assoc != null)
					qsArgs.Add(protocol.openid.assoc_handle, this.assoc.Handle);

				// Add on extension arguments
				foreach (var pair in OutgoingExtensions.GetArgumentsToSend(true))
					qsArgs.Add(pair.Key, pair.Value);

				var request = new IndirectMessageRequest(this.endpoint.ProviderEndpoint, qsArgs);
				return this.encoder.Encode(request);
			}
		}

		public void AddExtension(DotNetOpenId.Extensions.IExtensionRequest extension) {
			if (extension == null) throw new ArgumentNullException("extension");
			OutgoingExtensions.AddExtensionArguments(extension.TypeUri, extension.Serialize(this));
		}

		/// <summary>
		/// Adds given key/value pairs to the query that the provider will use in
		/// the request to return to the consumer web site.
		/// </summary>
		public void AddCallbackArguments(IDictionary<string, string> arguments) {
			if (arguments == null) throw new ArgumentNullException("arguments");
			foreach (var pair in arguments) {
				AddCallbackArguments(pair.Key, pair.Value);
			}
		}
		/// <summary>
		/// Adds a given key/value pair to the query that the provider will use in
		/// the request to return to the consumer web site.
		/// </summary>
		public void AddCallbackArguments(string key, string value) {
			if (string.IsNullOrEmpty(key)) throw new ArgumentNullException("key");
			if (ReturnToArgs.ContainsKey(key)) throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
				Strings.KeyAlreadyExists, key));
			ReturnToArgs.Add(key, value ?? "");
		}

		/// <summary>
		/// Redirects the user agent to the provider for authentication.
		/// Execution of the current page terminates after this call.
		/// </summary>
		/// <remarks>
		/// This method requires an ASP.NET HttpContext.
		/// </remarks>
		public void RedirectToProvider() {
			if (HttpContext.Current == null || HttpContext.Current.Response == null)
				throw new InvalidOperationException(Strings.CurrentHttpContextRequired);
			RedirectingResponse.Send();
		}
	}
}
