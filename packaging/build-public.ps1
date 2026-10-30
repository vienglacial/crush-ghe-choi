# Đóng gói bản phát hành công khai: dist\CrushGheChoi-<phiên bản>-win-x64.zip
# Chạy từ thư mục gốc dự án:  powershell -ExecutionPolicy Bypass -File packaging\build-public.ps1
# Cần .NET 10 SDK (trên máy này cài riêng ở %LOCALAPPDATA%\Microsoft\dotnet).

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not $env:DOTNET_ROOT) {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

[xml]$props = Get-Content "$root\Directory.Build.props"
$version = $props.Project.PropertyGroup.Version
$out = "$root\dist\CrushGheChoi"
$zip = "$root\dist\CrushGheChoi-$version-win-x64.zip"

# 1. Dọn sạch: build không tự xóa tệp thừa, nên ảnh cũ có thể còn sót trong bin/obj/dist.
foreach ($dir in @("$root\dist", "$root\src\CrushGheChoi\bin", "$root\src\CrushGheChoi\obj",
                   "$root\src\CrushGheChoi.Core\bin", "$root\src\CrushGheChoi.Core\obj",
                   "$root\tests\CrushGheChoi.Tests\bin", "$root\tests\CrushGheChoi.Tests\obj")) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}

# 2. Kiểm thử.
dotnet test tests\CrushGheChoi.Tests\CrushGheChoi.Tests.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Kiểm thử thất bại" }

# 3. Bản portable kèm runtime, một tệp exe, KHÔNG nén (bản nén tốn RAM gấp nhiều lần).
dotnet publish src\CrushGheChoi\CrushGheChoi.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Đóng gói thất bại" }
Copy-Item "$root\packaging\HUONG-DAN.txt" $out

# 4. Chỉ được có đúng những tệp này: không ảnh cá nhân, không model tách nền (tải về khi cần).
$expected = @(
    'CrushGheChoi.exe',
    'HUONG-DAN.txt',
    'THIRD-PARTY-NOTICES.txt',
    'Assets\Crush\default\manifest.json',
    'Assets\Crush\default\still.png',
    'Assets\Models\face_detection_yunet.LICENSE.txt',
    'Assets\Models\face_detection_yunet_2023mar.onnx'
) | Sort-Object
$actual = Get-ChildItem $out -Recurse -File | ForEach-Object { $_.FullName.Substring($out.Length + 1) } | Sort-Object
$diff = Compare-Object $expected $actual
if ($diff) {
    $diff | Format-Table | Out-String | Write-Host
    throw "Bản phát hành có tệp lạ hoặc thiếu tệp. Kiểm tra lại trước khi chia sẻ."
}

# Hình cạnh exe phải đúng là hình mẫu trong mã nguồn (hình nhúng trong exe dựng từ cùng tệp, sau bước dọn sạch ở trên).
$placeholder = (Get-FileHash "$root\src\CrushGheChoi\Assets\Crush\default\still.png").Hash
if ((Get-FileHash "$out\Assets\Crush\default\still.png").Hash -ne $placeholder) { throw "Hình mẫu trong bản phát hành không khớp." }

# 5. Nén.
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal -Force
$size = (Get-Item $zip).Length / 1MB
Write-Host ("Xong: {0} ({1:N0} MB)" -f $zip, $size)
