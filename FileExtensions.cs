//
// Licensed to Roland Pihlakas under one or more agreements.
// Roland Pihlakas licenses this file to you under the GNU Lesser General Public License, ver 2.1.
// See the LICENSE and copyrights.txt files for more information.
//

#define ASYNC
#define MS_IO_REDIST
using System;
#if NETSTANDARD
using System.Buffers;
#endif
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncToSyncCodeRoundtripSynchroniserMonitor
{
    public static partial class FileExtensions
    {
        public static long MaxStringSize = 0x3FFFFFDF;  //https://stackoverflow.com/questions/140468/what-is-the-maximum-possible-length-of-a-net-string

        //adapted from https://github.com/dotnet/runtime/blob/5ddc873d9ea6cd4bc6a935fec3057fe89a6932aa/src/libraries/System.IO.FileSystem/src/System/IO/File.cs

        //internal const int DefaultBufferSize = 4096;
        internal const int DefaultBufferSize = 1024 * 1024;     //roland

        private static Encoding s_UTF8NoBOM;

        // UTF-8 without BOM and with error detection. Same as the default encoding for StreamWriter.
        private static Encoding UTF8NoBOM => s_UTF8NoBOM ?? (s_UTF8NoBOM = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));

        // If we use the path-taking constructors we will not have FileOptions.Asynchronous set and
        // we will have asynchronous file access faked by the thread pool. We want the real thing.
        private static StreamReader AsyncStreamReader(string path, Encoding encoding)
        {
            FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        }

        public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default(CancellationToken), long maxFileSize = 0, int retryCount = 0)
            => ReadAllTextAsync(path, UTF8NoBOM, cancellationToken, maxFileSize, retryCount);

        public static async Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken), long maxFileSize = 0, int retryCount = 0)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));
            if (path.Length == 0)
                throw new ArgumentException("Argument_EmptyPath: {0}", nameof(path));


            retryCount = Math.Max(0, retryCount);
            for (int i = -1; i < retryCount; i++)   //roland
            {
                try    //roland
                {
                    return cancellationToken.IsCancellationRequested
                        ? await Task.FromCanceled<string>(cancellationToken)
                        : await InternalReadAllTextAsync(path, encoding, cancellationToken, maxFileSize);
                }
                catch (Exception ex) when (     //roland
                    ex is IOException
                    || ex is UnauthorizedAccessException    //can happen when a folder was just created     //TODO: abandon retries after a certain number of attempts in this case
                )
                {
                    //retry after delay

                    if (i + 1 < retryCount)     //do not sleep after last try
                    {
#if !NOASYNC
                        await Task.Delay(1000, cancellationToken);     //TODO: config file?
#else
                        cancellationToken.WaitHandle.WaitOne(1000);
#endif
                    }
                }
            }

            return null;
        }

        private static async Task<string> InternalReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken, long maxFileSize = 0)
        {
            Debug.Assert(!string.IsNullOrEmpty(path));
            Debug.Assert(encoding != null);

            char[] buffer = null;
            StreamReader sr = AsyncStreamReader(path, encoding);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();


                if (maxFileSize > 0)
                { 
                    long len = sr.BaseStream.Length;    //NB! the length might change during the code execution, so need to save it into separate variable

                    maxFileSize = Math.Min(MaxStringSize * sizeof(char), maxFileSize);
                    if (len > maxFileSize)
                    {
                        return null;    //TODO: return file size so that error message can report it
                    }
                }


#if NETSTANDARD
                buffer = ArrayPool<char>.Shared.Rent(sr.CurrentEncoding.GetMaxCharCount(DefaultBufferSize));
#else 
                buffer = new char[sr.CurrentEncoding.GetMaxCharCount(DefaultBufferSize)];
#endif
                StringBuilder sb = new StringBuilder();
                while (true)
                {
#if MS_IO_REDIST
                    int read = await sr.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
#else
                    int read = await sr.ReadAsync(new Memory<char>(buffer), cancellationToken).ConfigureAwait(false);
#endif
                    if (read == 0)
                    {
                        return sb.ToString();
                    }

                    sb.Append(buffer, 0, read);
                }
            }
            finally
            {
                sr.Dispose();
#if NETSTANDARD
                if (buffer != null)
                {
                    ArrayPool<char>.Shared.Return(buffer);
                }
#endif
            }
        }

        private static StreamWriter AsyncStreamWriter(string path, Encoding encoding, bool append)
        {
            FileStream stream = new FileStream(
                path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, DefaultBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return new StreamWriter(stream, encoding);
        }

        public static Task WriteAllTextAsync(string path, string contents, bool createTempFileFirst, CancellationToken cancellationToken = default(CancellationToken))
            => WriteAllTextAsync(path, contents, UTF8NoBOM, createTempFileFirst, cancellationToken);

        public static async Task WriteAllTextAsync(string path, string contents, Encoding encoding, bool createTempFileFirst, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));
            if (path.Length == 0)
                throw new ArgumentException("Argument_EmptyPath: {0}", nameof(path));

            var tempPath = path;
            if (createTempFileFirst)
                tempPath += ".tmp";

            while (true)    //roland
            {
                try    //roland
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    //if (cancellationToken.IsCancellationRequested)
                    //{
                    //    return Task.FromCanceled(cancellationToken);
                    //}

                    if (string.IsNullOrEmpty(contents))
                    {
                        new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read).Dispose();
                    }
                    else
                    { 
                        await InternalWriteAllTextAsync(AsyncStreamWriter(tempPath, encoding, append: false), contents, cancellationToken);
                    }


                    if (createTempFileFirst)
                    {
                        if (await Extensions.FSOperation(() => File.Exists(path), cancellationToken))
                        {
#pragma warning disable SEC0116 //Warning	SEC0116	Unvalidated file paths are passed to a file delete API, which can allow unauthorized file system operations (e.g. read, write, delete) to be performed on unintended server files.
                            await Extensions.FSOperation(() => File.Delete(path), cancellationToken);
#pragma warning restore SEC0116
                        }

                        await Extensions.FSOperation(() => File.Move(tempPath, path), cancellationToken);
                    }


                    return;     //exit while loop
                }
                catch (IOException)    //roland
                {
                    //retry after delay
#if !NOASYNC
                    await Task.Delay(1000, cancellationToken);     //TODO: config file?
#else
                    cancellationToken.WaitHandle.WaitOne(1000);
#endif
                }
            }
        }

        private static async Task InternalWriteAllTextAsync(StreamWriter sw, string contents, CancellationToken cancellationToken)
        {
            char[] buffer = null;
            try
            {
#if NETSTANDARD
                buffer = ArrayPool<char>.Shared.Rent(DefaultBufferSize);
#else 
                buffer = new char[DefaultBufferSize];
#endif
                int count = contents.Length;
                int index = 0;
                while (index < count)
                {
                    int batchSize = Math.Min(DefaultBufferSize, count - index);
                    contents.CopyTo(index, buffer, 0, batchSize);
#if MS_IO_REDIST
                    await sw.WriteAsync(buffer, 0, batchSize).ConfigureAwait(false);
#else
                    await sw.WriteAsync(new ReadOnlyMemory<char>(buffer, 0, batchSize), cancellationToken).ConfigureAwait(false);
#endif
                    index += batchSize;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await sw.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                sw.Dispose();
#if NETSTANDARD
                if (buffer != null)
                {
                    ArrayPool<char>.Shared.Return(buffer);
                }
#endif
            }
        }

        public static Task AppendAllTextAsync(string path, string contents, CancellationToken cancellationToken = default(CancellationToken))
            => AppendAllTextAsync(path, contents, UTF8NoBOM, cancellationToken);

        public static async Task AppendAllTextAsync(string path, string contents, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));
            if (path.Length == 0)
                throw new ArgumentException("Argument_EmptyPath", nameof(path));



            while (true)    //roland
            {
                try    //roland
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    //if (cancellationToken.IsCancellationRequested)
                    //{
                    //    return Task.FromCanceled(cancellationToken);
                    //}

                    if (string.IsNullOrEmpty(contents))
                    {
                        // Just to throw exception if there is a problem opening the file.
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read).Dispose();
                        return; // Task.CompletedTask;
                    }

                    await InternalWriteAllTextAsync(AsyncStreamWriter(path, encoding, append: true), contents, cancellationToken);

                    return;
                }
                catch (IOException)    //roland
                {
                    //retry after delay
#if !NOASYNC
                    await Task.Delay(1000, cancellationToken);     //TODO: config file?
#else
                    cancellationToken.WaitHandle.WaitOne(1000);
#endif
                }
            }
        }
    }
}
