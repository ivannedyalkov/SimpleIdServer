﻿// Copyright (c) SimpleIdServer. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using SimpleIdServer.OAuth;
using SimpleIdServer.OAuth.Jwt;
using SimpleIdServer.Uma.Api.Token.Fetchers;
using SimpleIdServer.Uma.Domains;
using SimpleIdServer.Uma.Extensions;
using SimpleIdServer.Uma.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace SimpleIdServer.Uma.Api
{
    [Route(UMAConstants.EndPoints.RequestsAPI)]
    public class RequestsAPIController : BaseAPIController
    {
        private readonly IUMAPendingRequestCommandRepository _umaPendingRequestCommandRepository;
        private readonly IUMAPendingRequestQueryRepository _umaPendingRequestQueryRepository;
        private readonly IUMAResourceCommandRepository _umaResourceCommandRepository;
        private readonly IUMAResourceQueryRepository _umaResourceQueryRepository;
        private readonly IEnumerable<IClaimTokenFormat> _claimTokenFormats;

        public RequestsAPIController(IUMAPendingRequestCommandRepository umaPendingRequestCommandRepository, IUMAPendingRequestQueryRepository umaPendingRequestQueryRepository, IUMAResourceCommandRepository umaResourceCommandRepository, IUMAResourceQueryRepository umaResourceQueryRepository, IEnumerable<IClaimTokenFormat> claimTokenFormats, IJwtParser jwtParser, IOptions<UMAHostOptions> umaHostoptions) : base(jwtParser, umaHostoptions)
        {
            _umaPendingRequestCommandRepository = umaPendingRequestCommandRepository;
            _umaPendingRequestQueryRepository = umaPendingRequestQueryRepository;
            _umaResourceCommandRepository = umaResourceCommandRepository;
            _umaResourceQueryRepository = umaResourceQueryRepository;
            _claimTokenFormats = claimTokenFormats;
        }
               
        [HttpGet(".search/me")]
        public Task<IActionResult> SearchRequests()
        {
            return CallOperationWithAuthenticatedUser(async (sub, payload) =>
            {
                var searchRequestParameter = ExtractSearchRequestParameter();
                var searchResult = await _umaPendingRequestQueryRepository.FindByRequester(sub, searchRequestParameter);
                return Serialize(searchRequestParameter, searchResult);
            });
        }

        [HttpGet(".search/received/me")]
        public Task<IActionResult> SearchReceivedRequests()
        {
            return CallOperationWithAuthenticatedUser(async (sub, payload) =>
            {
                var searchRequestParameter = ExtractSearchRequestParameter();
                var searchResult = await _umaPendingRequestQueryRepository.FindByOwner(sub, searchRequestParameter);
                return Serialize(searchRequestParameter, searchResult);
            });
        }        

        [HttpGet("confirm/{id}")]
        public Task<IActionResult> Confirm(string id)
        {
            return CallOperationWithAuthenticatedUser(async (sub, payload) =>
            {
                var pendingRequest = await _umaPendingRequestQueryRepository.FindByTicketIdentifierAndOwner(id, sub);
                if (pendingRequest == null)
                {
                    return this.BuildError(HttpStatusCode.Unauthorized, UMAErrorCodes.REQUEST_DENIED);
                }

                if (pendingRequest.Status != UMAPendingRequestStatus.TOBECONFIRMED)
                {
                    return this.BuildError(HttpStatusCode.BadRequest, ErrorCodes.INVALID_REQUEST, UMAErrorMessages.REQUEST_CANNOT_BE_CONFIRMED);
                }

                var resource = await _umaResourceQueryRepository.FindByIdentifier(pendingRequest.Resource.Id);
                foreach(var claimTokenFormat in _claimTokenFormats)
                {
                    resource.Permissions.Add(new UMAResourcePermission(Guid.NewGuid().ToString(), DateTime.UtcNow)
                    {
                        Claims = new List<UMAResourcePermissionClaim>
                        {
                            new UMAResourcePermissionClaim
                            {
                                Name = claimTokenFormat.GetSubjectName(),
                                Value = pendingRequest.Requester
                            }
                        },
                        Scopes = pendingRequest.Scopes.ToList()
                    });
                }

                pendingRequest.Confirm();
                _umaPendingRequestCommandRepository.Update(pendingRequest);
                _umaResourceCommandRepository.Update(resource);
                await _umaResourceCommandRepository.SaveChanges();
                await _umaPendingRequestCommandRepository.SaveChanges();
                return new NoContentResult();
            });
        }

        [HttpDelete("{id}")]
        public Task<IActionResult> Reject(string id)
        {
            return CallOperationWithAuthenticatedUser(async (sub, payload) =>
            {
                var pendingRequest = await _umaPendingRequestQueryRepository.FindByTicketIdentifierAndOwner(id, sub);
                if (pendingRequest == null)
                {
                    return this.BuildError(HttpStatusCode.Unauthorized, UMAErrorCodes.REQUEST_DENIED);
                }

                if (pendingRequest.Status != UMAPendingRequestStatus.TOBECONFIRMED)
                {
                    return this.BuildError(HttpStatusCode.BadRequest, ErrorCodes.INVALID_REQUEST, UMAErrorMessages.REQUEST_CANNOT_BE_CONFIRMED);
                }

                pendingRequest.Reject();
                _umaPendingRequestCommandRepository.Update(pendingRequest);
                await _umaPendingRequestCommandRepository.SaveChanges();
                return new NoContentResult();
            });
        }

        public static IActionResult Serialize(SearchRequestParameter searchRequestParameter, SearchResult<UMAPendingRequest> searchResult)
        {
            var result = new JObject
            {
                { "totalResults", searchResult.TotalResults },
                { "count", searchRequestParameter.Count },
                { "startIndex", searchRequestParameter.StartIndex },
                { "data", new JArray(searchResult.Records.Select(rec => Serialize(rec))) }
            };
            return new OkObjectResult(result);
        }

        public static JObject Serialize(UMAPendingRequest umaPendingRequest)
        {
            var result = new JObject
            {
                { "requester", umaPendingRequest.Requester },
                { "owner", umaPendingRequest.Owner },
                { "create_datetime", umaPendingRequest.CreateDateTime },
                { "ticket", umaPendingRequest.TicketId },
                { "scopes", new JArray(umaPendingRequest.Scopes) },
                { "status", umaPendingRequest.Status.ToString().ToLowerInvariant() },
                { "resource", ResourcesAPIController.Serialize(umaPendingRequest.Resource)}
            };
            return result;
        }
    }
}
