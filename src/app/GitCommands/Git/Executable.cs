using System.Diagnostics;
using System.Text;
using GitCommands.Logging;
using GitExtensions.Extensibility;
using GitUI;
using Microsoft.VisualStudio.Threading;

namespace GitCommands
{
    /// <inheritdoc />
    public sealed class Executable : IExecutable
    {
        private readonly string _workingDir;
        private readonly Func<string> _fileNameProvider;

        public Executable(string fileName, string workingDir = "")
            : this(() => fileName, workingDir)
        {
        }

        public Executable(Func<string> fileNameProvider, string workingDir = "", string prefixArguments = "")
        {
            _workingDir = workingDir;
            _fileNameProvider = fileNameProvider;
            PrefixArguments = prefixArguments;
        }

        public string WorkingDir => _workingDir;
        public string Command => _fileNameProvider();
        public string PrefixArguments { get; }

        public IProcess Start(ArgumentString arguments = default,
                              bool createWindow = false,
                              bool redirectInput = false,
                              bool redirectOutput = false,
                              Encoding? outputEncoding = null,
                              bool useShellExecute = false,
                              bool throwOnErrorExit = true,
                              CancellationToken cancellationToken = default)
        {
            // TODO should we set these on the child process only?
            EnvironmentConfiguration.SetEnvironmentVariables();

            string args = (arguments.Arguments ?? "").Replace("$QUOTE$", "\\\"");

            string fileName = _fileNameProvider();

            return new ProcessWrapper(fileName, PrefixArguments, args, _workingDir, createWindow, redirectInput, redirectOutput, outputEncoding, useShellExecute, throwOnErrorExit, cancellationToken);
        }

        public string GetWorkingDirectory() => _workingDir;

        #region ProcessWrapper

        /// <summary>
        /// Manages the lifetime of a process. The <see cref="Process"/> object has many members
        /// that throw at different times in the lifecycle of the process, such as after it is disposed. This class
        /// provides a simplified API that meets the need of this application via the <see cref="IProcess"/> interface.
        /// </summary>
        private sealed class ProcessWrapper : IProcess
        {
            // TODO should this use TaskCreationOptions.RunContinuationsAsynchronously
            private readonly TaskCompletionSource<int> _exitTaskCompletionSource = new();

            private readonly CancellationToken _cancellationToken;
            private readonly Encoding? _errorEncoding;
            private readonly MemoryStream? _errorOutputStream;
            private readonly ProcessOperation _logOperation;
            private readonly Process _process;
            private readonly bool _redirectInput;
            private readonly bool _redirectOutput;
            private readonly object _syncRoot = new();
            private readonly bool _throwOnErrorExit;

            private bool _disposed;
            private bool _exitHandlerRemoved;
            private string? _errorOutput;

            public ProcessWrapper(string fileName,
                                  string prefixArguments,
                                  string arguments,
                                  string workDir,
                                  bool createWindow,
                                  bool redirectInput,
                                  bool redirectOutput,
                                  Encoding? outputEncoding,
                                  bool useShellExecute,
                                  bool throwOnErrorExit,
                                  CancellationToken cancellationToken)
            {
                DebugHelpers.Assert(redirectOutput == (outputEncoding is not null), "redirectOutput == (outputEncoding is not null)");
                _redirectInput = redirectInput;
                _redirectOutput = redirectOutput;
                _throwOnErrorExit = throwOnErrorExit;
                _cancellationToken = cancellationToken;

                _errorEncoding = outputEncoding;
                if (throwOnErrorExit)
                {
                    _errorEncoding ??= Encoding.Default;
                }

                if (throwOnErrorExit || redirectOutput)
                {
                    _errorOutputStream = new MemoryStream();
                }

                _process = new Process
                {
                    EnableRaisingEvents = true,
                    StartInfo =
                    {
                        UseShellExecute = useShellExecute,
                        Verb = useShellExecute ? "open" : string.Empty,
                        ErrorDialog = false,
                        CreateNoWindow = !createWindow,
                        RedirectStandardInput = redirectInput,
                        RedirectStandardOutput = redirectOutput,
                        RedirectStandardError = _errorOutputStream is not null,
                        StandardOutputEncoding = outputEncoding,
                        StandardErrorEncoding = _errorEncoding,
                        FileName = fileName,
                        Arguments = $"{prefixArguments}{arguments}",
                        WorkingDirectory = workDir
                    }
                };

                _logOperation = CommandLog.LogProcessStart($"{fileName} {prefixArguments}".Trim(), arguments, workDir);

                _process.Exited += OnProcessExit;

                try
                {
                    _process.Start();

                    if (_errorOutputStream is not null)
                    {
                        _process.StandardError.BaseStream.CopyToAsync(_errorOutputStream, cancellationToken);
                    }

                    try
                    {
                        _logOperation.SetProcessId(_process.Id);
                    }
                    catch (InvalidOperationException ex) when (useShellExecute)
                    {
                        // _process.Start() has succeeded, ignore the failure getting the _process.Id
                        _logOperation.LogProcessEnd(ex);
                    }
                }
                catch (Exception ex)
                {
                    Dispose();

                    _logOperation.LogProcessEnd(ex);
                    throw new ExternalOperationException($"{fileName} {prefixArguments}".Trim(), arguments, workDir, innerException: ex);
                }
            }

            private void HandleProcessExit()
            {
                try
                {
                    int exitCode = _process.ExitCode;
                    string? errorOutput = ReadErrorOutput();
                    _logOperation.LogProcessEnd(exitCode, errorOutput);

                    if (_throwOnErrorExit && exitCode != 0)
                    {
                        string errorMessage = errorOutput?.Length is > 0 ? errorOutput : "External program returned non-zero exit code.";
                        Exception ex
                            = new ExternalOperationException(command: _process.StartInfo.FileName,
                                    _process.StartInfo.Arguments,
                                    _process.StartInfo.WorkingDirectory,
                                    exitCode,
                                    new Exception(errorMessage));
                        if (exitCode == NativeMethods.STATUS_CONTROL_C_EXIT)
                        {
                            ex = new OperationCanceledException("Ctrl+C pressed or console closed", ex);
                        }

                        _exitTaskCompletionSource.TrySetException(ex);
                    }

                    _exitTaskCompletionSource.TrySetResult(exitCode);
                }
                catch (Exception ex)
                {
                    _logOperation.LogProcessEnd(ex);
                    _exitTaskCompletionSource.TrySetException(ex);
                }

                return;

                string? ReadErrorOutput()
                {
                    if (_errorOutputStream is null)
                    {
                        return null;
                    }

                    try
                    {
                        _errorOutput = _errorEncoding.GetString(_errorOutputStream.GetBuffer(), 0, (int)_errorOutputStream.Length);

                        return _errorOutput.Trim();
                    }
                    catch (Exception ex)
                    {
                        return $"Failed to read: {ex}";
                    }
                }
            }

            private void OnProcessExit(object sender, EventArgs eventArgs)
            {
                lock (_syncRoot)
                {
                    // The Exited event can be raised after the process is disposed, however
                    // if the Process is disposed then reading ExitCode will throw.
                    if (!_disposed && !_exitHandlerRemoved)
                    {
                        _process.Exited -= OnProcessExit;
                        _exitHandlerRemoved = true;
                        HandleProcessExit();
                    }
                }
            }

            public StreamWriter StandardInput
            {
                get
                {
                    if (!_redirectInput)
                    {
                        throw new InvalidOperationException("Process was not created with redirected input.");
                    }

                    return _process.StandardInput;
                }
            }

            public StreamReader StandardOutput
            {
                get
                {
                    if (!_redirectOutput)
                    {
                        throw new InvalidOperationException("Process was not created with redirected output.");
                    }

                    return _process.StandardOutput;
                }
            }

            public string StandardError
            {
                get
                {
                    if (_errorOutput is null)
                    {
                        throw new InvalidOperationException("Process was not created with redirected output.");
                    }

                    return _errorOutput;
                }
            }

            public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

            public void WaitForInputIdle() => _process.WaitForInputIdle();

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            public Task<int> WaitForExitAsync() => _exitTaskCompletionSource.Task;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

            public Task<int> WaitForExitAsync(CancellationToken token) => WaitForExitAsync().WithCancellation(token);

            public int WaitForExit() => ThreadHelper.JoinableTaskFactory.Run(WaitForExitAsync);

            public void Dispose()
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;

                    if (!_exitHandlerRemoved)
                    {
                        // HandleProcessExit directly, do not rely on event execution
                        _process.Exited -= OnProcessExit;
                        _exitHandlerRemoved = true;

                        try
                        {
                            if (_process.HasExited)
                            {
                                HandleProcessExit();
                            }
                            else if (_cancellationToken.IsCancellationRequested)
                            {
                                // Directly kill the process because Ctrl+C does not reach the git process how we start it
                                _process.Kill();

                                OperationCanceledException ex = new("Process killed");
                                _logOperation.LogProcessEnd(ex);
                                _exitTaskCompletionSource.TrySetException(ex);
                            }
                            else
                            {
                                _exitTaskCompletionSource.TrySetCanceled();
                            }
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine(ex);
                        }
                    }
                }

                _process.Dispose();

                _logOperation.NotifyDisposed();

                _errorOutputStream?.Dispose();
            }
        }

        #endregion
    }
}
