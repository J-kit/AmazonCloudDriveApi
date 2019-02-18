using System;
using System.IO;
using System.Text;

namespace Azi.Amazon.CloudDrive.Http
{
    internal class MultipartBoundary
    {
        private static Encoding Encoding => new UTF8Encoding(false, true);

        public Guid Boundary { get; }

        public Stream Postfix { get; }

        private SendFileInfo _fileInfo;

        public MultipartBoundary(SendFileInfo fileInfo)
        {
            _fileInfo = fileInfo;
            Boundary = Guid.NewGuid();

            Postfix = GetPostFix();
        }

        public Stream GetPrefix(Stream fileStream)
        {
            var result = new MemoryStream(500);
            using (var writer = new StreamWriter(result, Encoding, 16, true))
            {
                if (_fileInfo.Parameters != null)
                {
                    foreach (var pair in _fileInfo.Parameters)
                    {
                        writer.Write($"--{Boundary}\r\n");
                        writer.Write($"Content-Disposition: form-data; name=\"{pair.Key}\"\r\n\r\n{pair.Value}\r\n");
                    }
                }

                writer.Write($"--{Boundary}\r\n");
                writer.Write($"Content-Disposition: form-data; name=\"{_fileInfo.FormName}\"; filename={_fileInfo.FileName}\r\n");
                writer.Write($"Content-Type: application/octet-stream\r\n");

                writer.Write($"Content-Length: {fileStream.Length}\r\n\r\n");
            }

            result.Position = 0;
            return result;
        }

        private Stream GetPostFix()
        {
            var result = new MemoryStream(255);
            using (var writer = new StreamWriter(result, Encoding, 16, true))
            {
                writer.Write($"\r\n--{Boundary}--\r\n");
            }

            result.Position = 0;
            return result;
        }
    }
}