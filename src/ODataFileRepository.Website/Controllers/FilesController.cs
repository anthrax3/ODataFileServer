﻿using Microsoft.OData.Core;
using ODataFileRepository.Infrastructure.ODataExtensions;
using ODataFileRepository.Website.DataAccess.Contracts;
using ODataFileRepository.Website.DataAccess.Exceptions;
using ODataFileRepository.Website.Infrastructure.ODataExtensions;
using ODataFileRepository.Website.ServiceModels;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.OData;
using System.Web.OData.Formatter.Serialization;
using System.Web.OData.Routing;

namespace ODataFileRepository.Website.Controllers
{
    [ExtendedODataFormatting, ODataRouting]
    public sealed class FilesController : ApiController, IMediaStreamReferenceProvider
    {
        private readonly IFileDataAccess _fileDataAccess;

        public FilesController(
            IFileDataAccess fileDataAccess)
        {
            _fileDataAccess = fileDataAccess;
        }
        
        public async Task<IHttpActionResult> Get()
        {
            var files = await _fileDataAccess.GetAllAsync();

            return Ok(files.Select(f => new File(f)));
        }
        
        public async Task<IHttpActionResult> Get([FromODataUri] string key)
        {
            var fileMetadata = await _fileDataAccess.GetMetadataAsync(key);
            var file = fileMetadata != null ? new File(fileMetadata) : null;

            if (file == null)
            {
                return NotFound();
            }

            return Ok(file);
        }

        public async Task<IHttpActionResult> GetValue([FromODataUri] string key)
        {
            var fileMetadata = await _fileDataAccess.GetMetadataAsync(key);

            if (fileMetadata == null)
            {
                return NotFound();
            }

            var fileStream = await _fileDataAccess.GetStreamAsync(key);

            var range = Request.Headers.Range;

            if (range == null)
            {
                // if the range header is present but null, then the header value must be invalid
                if (Request.Headers.Contains("Range"))
                {
                    return StatusCode(HttpStatusCode.RequestedRangeNotSatisfiable);
                }

                // if no range was requested, return the entire stream

                var response = Request.CreateResponse(HttpStatusCode.OK);

                response.Headers.AcceptRanges.Add("bytes");
                response.Content = new StreamContent(fileStream);
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(fileMetadata.MediaType);

                return ResponseMessage(response);
            }
            else
            {
                if (!fileStream.CanSeek)
                {
                    return StatusCode(HttpStatusCode.RequestedRangeNotSatisfiable);
                }

                var response = Request.CreateResponse(HttpStatusCode.PartialContent);
                response.Headers.AcceptRanges.Add("bytes");

                try
                {
                    // return the requested range(s)
                    response.Content = new ByteRangeStreamContent(fileStream, range, fileMetadata.MediaType);
                }
                catch (InvalidByteRangeException)
                {
                    response.Dispose();
                    throw;
                }

                // change status code if the entire stream was requested
                if (response.Content.Headers.ContentLength.Value == fileStream.Length)
                {
                    response.StatusCode = HttpStatusCode.OK;
                }

                return ResponseMessage(response);
            }
        }

        public async Task<IHttpActionResult> Post()
        {
            var contentTypeHeader = Request.Content.Headers.ContentType;

            if (contentTypeHeader == null || contentTypeHeader.MediaType == null)
            {
                return StatusCode(HttpStatusCode.BadRequest);
            }

            var file = new File
            {
                FullName = Guid.NewGuid().ToString("N").ToLowerInvariant(),
                MediaType = contentTypeHeader.MediaType
            };

            var stream = await Request.Content.ReadAsStreamAsync();

            await _fileDataAccess.CreateAsync(file.FullName, file.MediaType, stream);

            return Ok(file);
        }

        public async Task<IHttpActionResult> Put(File file)
        {
            try
            {
                await _fileDataAccess.UpdateMetadataAsync(file);

                return Ok(file);
            }
            catch (ResourceNotFoundException)
            {
                return NotFound();
            }
        }

        public async Task<IHttpActionResult> Patch([FromODataUri] string key, Delta<File> fileDelta)
        {
            try
            {
                var fileMetadata = await _fileDataAccess.GetMetadataAsync(key);

                fileDelta.Patch(new File(fileMetadata));

                await _fileDataAccess.UpdateMetadataAsync(fileMetadata);

                return Ok(fileMetadata);
            }
            catch (ResourceNotFoundException)
            {
                return NotFound();
            }
        }

        [HttpDelete]
        [ODataRoute("files({key})")]
        [ODataRoute("files({key})/$value")]
        public async Task<IHttpActionResult> Delete([FromODataUri] string key)
        {
            try
            {
                await _fileDataAccess.DeleteAsync(key);

                return StatusCode(HttpStatusCode.NoContent);
            }
            catch (ResourceNotFoundException)
            {
                return NotFound();
            }
        }

        protected override void Initialize(HttpControllerContext controllerContext)
        {
            base.Initialize(controllerContext);

            Request.SetMediaStreamReferenceProvider(this);
        }

        ODataStreamReferenceValue IMediaStreamReferenceProvider.GetMediaStreamReference(
            EntityInstanceContext entity,
            ODataSerializerContext context)
        {
            var file = entity.EntityInstance as File;

            if (file == null)
            {
                return null;
            }

            return new ODataStreamReferenceValue
            {
                //ReadLink = new Uri("files('" + file.FullName + "')/$value2", UriKind.Relative),
                //EditLink = new Uri("files('" + file.FullName + "')/$value3", UriKind.Relative),
                ContentType = file.MediaType
            };
        }
    }
}