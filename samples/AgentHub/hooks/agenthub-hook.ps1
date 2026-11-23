param([Parameter(Mandatory=$true)][string]$Agent)
$ErrorActionPreference = 'Stop'
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $identity = [Environment]::UserDomainName + '\' + [Environment]::UserName
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity))).Replace('-','').Substring(0,16) }
    finally { $sha.Dispose() }
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', ('WinIsland.AgentHub.' + $hash), [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(750)
        $envelope = @{ agent = $Agent; payload = $payload } | ConvertTo-Json -Depth 100 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($envelope)
        if ($bytes.Length -gt 262144) { throw 'Hook payload too large' }
        $prefix = [BitConverter]::GetBytes([int]$bytes.Length)
        $pipe.Write($prefix,0,4); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush()
        $reader = New-Object IO.StreamReader($pipe)
        try {
            $read = $reader.ReadLineAsync()
            if ($read.Wait(600000) -and $read.Result) { [Console]::Out.WriteLine($read.Result) }
            else { [Console]::Out.WriteLine('{}') }
        } finally { $reader.Dispose() }
    } finally { $pipe.Dispose() }
} catch { [Console]::Out.WriteLine('{}') }
# No decision means the client's normal approval UI takes over, including when WinIsland is absent.
exit 0
