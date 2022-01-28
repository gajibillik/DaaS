﻿// -----------------------------------------------------------------------
// <copyright file="StorageValidator.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DiagnosticsExtension.Controllers;
using DiagnosticsExtension.Models.ConnectionStringValidator.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiagnosticsExtension.Models.ConnectionStringValidator
{
    public class StorageValidator : IConnectionStringValidator
    {
        public string ProviderName => "Microsoft.WindowsAzure.Storage";

        public ConnectionStringType Type => ConnectionStringType.StorageAccount;

        public Task<bool> IsValidAsync(string connStr)
        {
            throw new NotImplementedException();
        }

        public async Task<ConnectionStringValidationResult> ValidateAsync(string connStr, string clientId = null)
        {
            var response = new ConnectionStringValidationResult(Type);

            try
            {
                var result = await TestConnectionString(connStr, null, clientId);
                if (result.Succeeded)
                {
                    response.Status = ConnectionStringValidationResult.ResultStatus.Success;
                }
                else
                {
                    throw new Exception("Unexpected state reached: result.Succeeded == false is unexpected!");
                }
            }
            catch (Exception e)
            {
                if (e is MalformedConnectionStringException)
                {
                    response.Status = ConnectionStringValidationResult.ResultStatus.MalformedConnectionString;
                }
                else if (e is EmptyConnectionStringException)
                {
                    response.Status = ConnectionStringValidationResult.ResultStatus.EmptyConnectionString;
                }
                else if (e.InnerException != null &&
                         e.InnerException.Message.Contains("The remote name could not be resolved"))
                {
                    response.Status = ConnectionStringValidationResult.ResultStatus.DnsLookupFailed;
                }
                //else if (e is StorageException)
                //{
                //    if (((StorageException)e).RequestInformation.HttpStatusCode == 401)
                //    {
                //        response.Status = ConnectionStringValidationResult.ResultStatus.AuthFailure;
                //    }
                //    else if (((StorageException)e).RequestInformation.HttpStatusCode == 403)
                //    {
                //        response.Status = ConnectionStringValidationResult.ResultStatus.Forbidden;
                //    }
                //}
                else
                {
                    response.Status = ConnectionStringValidationResult.ResultStatus.UnknownError;
                }
                response.Exception = e;
            }

            return response;
        }

        public Task<TestConnectionData> TestConnectionString(string connectionString, string name, string clientId)
        {            
            BlobServiceClient client = null;           
                try
                {                    
                    client = new BlobServiceClient(connectionString);
                }
                catch (ArgumentNullException e)
                {
                    throw new EmptyConnectionStringException(e.Message, e);
                }
                catch (Exception e)
                {
                    throw new MalformedConnectionStringException(e.Message, e);
                }

            client.GetBlobContainersAsync();
            var resultSegment =
                client.GetBlobContainers(BlobContainerTraits.Metadata, null, default)
                .AsPages(default, 10);
            //connection autherization check
            resultSegment.Single();

            TestConnectionData data = new TestConnectionData
            {
                ConnectionString = client.ToString(),
                Succeeded = true
            }; 

            return Task.FromResult(data);
        }
        async public Task<ConnectionStringValidationResult> ValidateViaAppsettingAsync(string appsettingName, string entityName)
        {
            throw new EmptyConnectionStringException();
        }
    }
}
