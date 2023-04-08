﻿using Aguacongas.IdentityServer.EntityFramework.Store;
using Aguacongas.IdentityServer.Store.Entity;
using Aguacongas.TheIdServer.Data;
using Aguacongas.TheIdServer.IntegrationTest.BlazorApp;
using Aguacongas.TheIdServer.UI;
using Duende.IdentityServer;
using Duende.IdentityServer.Services;
using IdentityModel;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ConstrainedExecution;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using ISModels = Duende.IdentityServer.Models;

namespace Aguacongas.TheIdServer.Integration.Duende.Test.Controllers;

[Collection(BlazorAppCollection.Name)]
public class Saml2PControllerTest
{
    private WebApplicationFactory<AccountController> _factory;

    public Saml2PControllerTest(TheIdServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Metadata_should_return_saml2_metadata()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/saml2p/metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var descriptor = new EntityDescriptor();
        descriptor.ReadIdPSsoDescriptor(content);

        Assert.NotNull(descriptor.IdPSsoDescriptor);
    }

    [Fact]
    public async Task Login_should_return_saml2_form_post_result_when_user_user_found()
    {
        var userSessionMock = new Mock<IUserSession>();
        var sub = Guid.NewGuid().ToString();
        var name = Guid.NewGuid().ToString();
        var user = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                        new Claim("name", name),
                        new Claim("sub", sub),
                        new Claim("amr", Guid.NewGuid().ToString())
                },
                "saml2p",
                "name",
                "role"));
        userSessionMock.Setup(m => m.GetUserAsync()).ReturnsAsync(user);

        var profileServiceMock = new Mock<IProfileService>();
        profileServiceMock.Setup(m => m.GetProfileDataAsync(It.IsAny<ISModels.ProfileDataRequestContext>()))
            .Callback<ISModels.ProfileDataRequestContext>(ctx => ctx.IssuedClaims = new List<Claim>
            {
                    new Claim(JwtClaimTypes.Name, name),
                    new Claim(JwtClaimTypes.Subject, sub),
                    new Claim("http://exemple.com", Guid.NewGuid().ToString()),
            })
            .Returns(Task.CompletedTask);

        _factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddTransient(p => userSessionMock.Object)
                .AddTransient(p => profileServiceMock.Object);
        }));

        var certificate = new X509Certificate2("itfoxtec.identity.saml2.testwebappcore_Certificate.pfx", "!QAZ2wsx");
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConfigurationDbContext>();

        var issuer = $"urn:{Guid.NewGuid()}";
        await context.Clients.AddAsync(new Client
        {
            Id = issuer,
            Enabled = true,
            ProtocolType = IdentityServerConstants.ProtocolTypes.Saml2p,
            RedirectUris = new[]
            {
                new ClientUri
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow,
                    Kind = UriKinds.Acs,
                    Uri = "http://exemple.com"
                }
            },
            ClientSecrets = new[]
            {
                new ClientSecret
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow,
                    Type = "X509CertificateBase64",
                    Value = Convert.ToBase64String(certificate.Export(X509ContentType.Cert))
                }
            }
        }).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);

        var identityContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await identityContext.Users.AddAsync(new User
        {
            Id = sub,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString()
        }).ConfigureAwait(false);
        await identityContext.SaveChangesAsync().ConfigureAwait(false);

        var config = new Saml2Configuration
        {
            Issuer = issuer,
            SignatureAlgorithm = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256",
            SigningCertificate = certificate,
        };
        config.AllowedAudienceUris.Add(issuer);

        var entityDiscriptor = await GetIpdDescriptorAsync().ConfigureAwait(false);
        config.AllowedIssuer = entityDiscriptor.EntityId;
        var idPSsoDescriptor = entityDiscriptor.IdPSsoDescriptor;
        config.SingleSignOnDestination = idPSsoDescriptor.SingleSignOnServices.First().Location;
        config.SingleLogoutDestination = idPSsoDescriptor.SingleLogoutServices.First().Location;
        foreach (var signingCertificate in idPSsoDescriptor.SigningCertificates)
        {
            if (signingCertificate.IsValidLocalTime())
            {
                config.SignatureValidationCertificates.Add(signingCertificate);
            }
        }
        if (idPSsoDescriptor.WantAuthnRequestsSigned.HasValue)
        {
            config.SignAuthnRequest = idPSsoDescriptor.WantAuthnRequestsSigned.Value;
        }

        var binding = new Saml2RedirectBinding();
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        binding.SetRelayStateQuery(new Dictionary<string, string?>
        {
            ["ReturnUrl"] = client.BaseAddress?.ToString()
        });

        binding.Bind(new Saml2AuthnRequest(config)
        {
            Subject = new Subject { NameID = new NameID { ID = "abcd" } },
            NameIdPolicy = new NameIdPolicy { AllowCreate = true, Format = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent" },
        });

        using var response = await client.GetAsync(binding.RedirectLocation).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.NotNull(content);
    }

    [Fact]
    public async Task Login_should_return_saml2_redirect_when_user_and_use_acs_artifact_found()
    {
        var userSessionMock = new Mock<IUserSession>();
        var sub = Guid.NewGuid().ToString();
        var name = Guid.NewGuid().ToString();
        var user = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                        new Claim("name", name),
                        new Claim("sub", sub),
                        new Claim("amr", Guid.NewGuid().ToString())
                },
                "saml2p",
                "name",
                "role"));
        userSessionMock.Setup(m => m.GetUserAsync()).ReturnsAsync(user);

        var profileServiceMock = new Mock<IProfileService>();
        profileServiceMock.Setup(m => m.GetProfileDataAsync(It.IsAny<ISModels.ProfileDataRequestContext>()))
            .Callback<ISModels.ProfileDataRequestContext>(ctx => ctx.IssuedClaims = new List<Claim>
            {
                    new Claim(JwtClaimTypes.Name, name),
                    new Claim(JwtClaimTypes.Subject, sub),
                    new Claim("http://exemple.com", Guid.NewGuid().ToString()),
            })
            .Returns(Task.CompletedTask);

        _factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddTransient(p => userSessionMock.Object)
                .AddTransient(p => profileServiceMock.Object);
        }));

        var certificate = new X509Certificate2("itfoxtec.identity.saml2.testwebappcore_Certificate.pfx", "!QAZ2wsx");
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConfigurationDbContext>();

        var issuer = $"urn:{Guid.NewGuid()}";
        await context.Clients.AddAsync(new Client
        {
            Id = issuer,
            Enabled = true,
            ProtocolType = IdentityServerConstants.ProtocolTypes.Saml2p,
            RedirectUris = new[]
            {
                new ClientUri
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow,
                    Kind = UriKinds.Acs,
                    Uri = "http://exemple.com"
                }
            },
            ClientSecrets = new[]
            {
                new ClientSecret
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow,
                    Type = "X509CertificateBase64",
                    Value = Convert.ToBase64String(certificate.Export(X509ContentType.Cert))
                }
            },
            Properties = new[]
            {
                new ClientProperty
                {
                    Id = Guid.NewGuid().ToString(),
                    Key = nameof(IdentityServer.Saml2p.Duende.Services.Store.RelyingParty.UseAcsArtifact),
                    Value = true.ToString()
                }
            }
        }).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);

        var identityContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await identityContext.Users.AddAsync(new User
        {
            Id = sub,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString()
        }).ConfigureAwait(false);
        await identityContext.SaveChangesAsync().ConfigureAwait(false);

        var config = new Saml2Configuration
        {
            Issuer = issuer,
            SignatureAlgorithm = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256",
            SigningCertificate = certificate,
        };
        config.AllowedAudienceUris.Add(issuer);

        var entityDiscriptor = await GetIpdDescriptorAsync().ConfigureAwait(false);
        config.AllowedIssuer = entityDiscriptor.EntityId;
        var idPSsoDescriptor = entityDiscriptor.IdPSsoDescriptor;
        config.SingleSignOnDestination = idPSsoDescriptor.SingleSignOnServices.First().Location;
        config.SingleLogoutDestination = idPSsoDescriptor.SingleLogoutServices.First().Location;
        config.ArtifactResolutionService = idPSsoDescriptor.ArtifactResolutionServices
            .Select(s => new Saml2IndexedEndpoint { Index = s.Index, Location = s.Location })
            .First();
        config.CertificateValidationMode = X509CertificateValidationMode.None;

        foreach (var signingCertificate in idPSsoDescriptor.SigningCertificates)
        {
            if (signingCertificate.IsValidLocalTime())
            {
                config.SignatureValidationCertificates.Add(signingCertificate);
            }
        }
        if (idPSsoDescriptor.WantAuthnRequestsSigned.HasValue)
        {
            config.SignAuthnRequest = idPSsoDescriptor.WantAuthnRequestsSigned.Value;
        }

        var binding = new Saml2RedirectBinding();
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        binding.SetRelayStateQuery(new Dictionary<string, string?>
        {
            ["ReturnUrl"] = client.BaseAddress?.ToString()
        });

        binding.Bind(new Saml2AuthnRequest(config)
        {
            Subject = new Subject { NameID = new NameID { ID = "abcd" } },
            NameIdPolicy = new NameIdPolicy { AllowCreate = true, Format = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent" },
        });

        using var response = await client.GetAsync(binding.RedirectLocation).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        var location = response.Headers.Location;
        Assert.NotNull(location);
        var query = location.Query.Substring(1);

        var nv = new NameValueCollection();
        foreach(var segment in query.Split('&'))
        {
            var kv = segment.Split('=');
            nv.Add(kv[0], Uri.UnescapeDataString(kv[1]));
        }

        var genericHttpRequest = new ITfoxtec.Identity.Saml2.Http.HttpRequest
        {
            Method = "GET",
            QueryString = query,
            Query = nv
        };

        var artifactBinding = new Saml2ArtifactBinding();
        var saml2ArtifactResolve = new Saml2ArtifactResolve(config);

        artifactBinding.Unbind(genericHttpRequest, saml2ArtifactResolve);

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(m => m.CreateClient(It.IsAny<string>())).Returns(client);

        var soapEnvelope = new Saml2SoapEnvelope();
        var saml2AuthnResponse = new Saml2AuthnResponse(config);

        await soapEnvelope.ResolveAsync(httpFactoryMock.Object, saml2ArtifactResolve, saml2AuthnResponse)
            .ConfigureAwait(false);

        var relayStateQuery = artifactBinding.GetRelayStateQuery();

        Assert.NotEmpty(relayStateQuery);
    }

    [Fact]
    public async Task Logout_should_return_saml2_form_post_result_when_user_user_found()
    {
        var userSessionMock = new Mock<IUserSession>();
        var sub = Guid.NewGuid().ToString();
        var name = Guid.NewGuid().ToString();
        var user = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                        new Claim("http://schemas.itfoxtec.com/ws/2014/02/identity/claims/saml2nameid", name),
                        new Claim("sub", sub),
                        new Claim("amr", Guid.NewGuid().ToString())
                },
                "saml2p",
                "name",
                "role"));
        userSessionMock.Setup(m => m.GetUserAsync()).ReturnsAsync(user);

        var profileServiceMock = new Mock<IProfileService>();
        profileServiceMock.Setup(m => m.GetProfileDataAsync(It.IsAny<ISModels.ProfileDataRequestContext>()))
            .Callback<ISModels.ProfileDataRequestContext>(ctx => ctx.IssuedClaims = new List<Claim>
            {
                    new Claim(JwtClaimTypes.Name, name),
                    new Claim(JwtClaimTypes.Subject, sub),
                    new Claim("http://exemple.com", Guid.NewGuid().ToString()),
            })
            .Returns(Task.CompletedTask);

        _factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddTransient(p => userSessionMock.Object)
                .AddTransient(p => profileServiceMock.Object);
        }));

        var certificate = new X509Certificate2("itfoxtec.identity.saml2.testwebappcore_Certificate.pfx", "!QAZ2wsx");
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConfigurationDbContext>();

        var issuer = $"urn:{Guid.NewGuid()}";
        await context.Clients.AddAsync(new Client
        {
            Id = issuer,
            Enabled = true,
            ProtocolType = IdentityServerConstants.ProtocolTypes.Saml2p,
            RedirectUris = new[]
            {
                new ClientUri
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow,
                    Kind = UriKinds.Acs,
                    Uri = "http://exemple.com"
                }
            },
            ClientSecrets = new[]
            {
                new ClientSecret
                {
                    Id = Guid.NewGuid().ToString(),
                    CreatedAt = DateTime.UtcNow,
                    Type = "X509CertificateBase64",
                    Value = Convert.ToBase64String(certificate.Export(X509ContentType.Cert))
                }
            }
        }).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);

        var identityContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await identityContext.Users.AddAsync(new User
        {
            Id = sub,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString()
        }).ConfigureAwait(false);
        await identityContext.SaveChangesAsync().ConfigureAwait(false);

        var config = new Saml2Configuration
        {
            Issuer = issuer,
            SignatureAlgorithm = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256",
            SigningCertificate = certificate,
        };
        config.AllowedAudienceUris.Add(issuer);

        var entityDiscriptor = await GetIpdDescriptorAsync().ConfigureAwait(false);
        config.AllowedIssuer = entityDiscriptor.EntityId;
        var idPSsoDescriptor = entityDiscriptor.IdPSsoDescriptor;
        config.SingleSignOnDestination = idPSsoDescriptor.SingleSignOnServices.First().Location;
        config.SingleLogoutDestination = idPSsoDescriptor.SingleLogoutServices.First().Location;
        foreach (var signingCertificate in idPSsoDescriptor.SigningCertificates)
        {
            if (signingCertificate.IsValidLocalTime())
            {
                config.SignatureValidationCertificates.Add(signingCertificate);
            }
        }
        if (idPSsoDescriptor.WantAuthnRequestsSigned.HasValue)
        {
            config.SignAuthnRequest = idPSsoDescriptor.WantAuthnRequestsSigned.Value;
        }

        var binding = new Saml2PostBinding();
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        binding.SetRelayStateQuery(new Dictionary<string, string?>
        {
            ["ReturnUrl"] = client.BaseAddress?.ToString()
        });

        binding.Bind(new Saml2LogoutRequest(config, user));
        using var logoutContent = new FormUrlEncodedContent(new Dictionary<string, string> 
        {
            ["SAMLRequest"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.XmlDocument.OuterXml)),
            ["RelayState"] = binding.RelayState
        });
        using var response = await client.PostAsync("/saml2p/logout", logoutContent).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.NotNull(content);
    }

    private async Task<EntityDescriptor> GetIpdDescriptorAsync()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/saml2p/metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var descriptor = new EntityDescriptor();
        descriptor.ReadIdPSsoDescriptor(content);

        return descriptor;
    }
}
