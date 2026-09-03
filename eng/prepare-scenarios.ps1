param(
    [Parameter(Mandatory)]
    [string]$FixturePath,
    [Parameter(Mandatory)]
    [string]$SystemRuntimePath,
    [Parameter(Mandatory)]
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

$scenarioFixture = (Resolve-Path -LiteralPath $FixturePath).Path
$scenarioRuntime = (Resolve-Path -LiteralPath $SystemRuntimePath).Path
$scenarioRepository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$scenarioRoot = Join-Path $scenarioRepository 'tests/scenarios'
$approvedFixtureSha256 = '0f22ceda1bb8d75701a962c325b68f9dc0fd202018bea4e0f170a48b88da3fa1'
if (-not $scenarioFixture.StartsWith($scenarioRepository, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture path is outside the repository.'
}

function Get-LowerHash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if ((Get-LowerHash $scenarioFixture) -ne $approvedFixtureSha256) {
    throw 'The fixture DLL does not match the Architecture Gate approved artifact SHA-256.'
}

foreach ($scenarioName in @('relevant', 'irrelevant')) {
    $scenarioDirectory = Join-Path $scenarioRoot $scenarioName
    $generatorDirectory = Join-Path $scenarioDirectory 'generator'
    $referenceDirectory = Join-Path $scenarioDirectory 'references'
    New-Item -ItemType Directory -Path $generatorDirectory,$referenceDirectory -Force | Out-Null

    $generatorTarget = Join-Path $generatorDirectory 'SourceGenAuditor.Fixtures.dll'
    $runtimeTarget = Join-Path $referenceDirectory 'System.Runtime.dll'
    Copy-Item -LiteralPath $scenarioFixture -Destination $generatorTarget -Force
    Copy-Item -LiteralPath $scenarioRuntime -Destination $runtimeTarget -Force

    $baselineSource = Join-Path $scenarioDirectory 'inputs/Input.A.cs'
    $replacementSource = Join-Path $scenarioDirectory 'inputs/Input.B.cs'
    $isRelevant = $scenarioName -eq 'relevant'
    $scenarioDocument = [ordered]@{
        schemaVersion = 1
        id = if ($isRelevant) { 'class-name-is-relevant' } else { 'comment-is-irrelevant' }
        generator = [ordered]@{
            assemblyPath = 'generator/SourceGenAuditor.Fixtures.dll'
            sha256 = Get-LowerHash $generatorTarget
            typeName = 'SourceGenAuditor.Fixtures.ConfigurableIncrementalGenerator'
        }
        baseline = [ordered]@{
            assemblyName = "SourceGenAuditorScenario.$scenarioName"
            sources = @([ordered]@{
                logicalPath = 'Input.cs'
                path = 'inputs/Input.A.cs'
                sha256 = Get-LowerHash $baselineSource
            })
            references = @([ordered]@{
                path = 'references/System.Runtime.dll'
                sha256 = Get-LowerHash $runtimeTarget
            })
            parseOptions = [ordered]@{
                languageVersion = '14.0'
                documentationMode = 'parse'
                preprocessorSymbols = @()
            }
            compilationOptions = [ordered]@{
                outputKind = 'dynamicallyLinkedLibrary'
                nullableContext = 'enable'
                allowUnsafe = $false
            }
        }
        mutation = [ordered]@{
            id = if ($isRelevant) { 'replace-class-name' } else { 'replace-comment' }
            kind = 'replaceSourceText'
            targetLogicalPath = 'Input.cs'
            replacementPath = 'inputs/Input.B.cs'
            replacementSha256 = Get-LowerHash $replacementSource
            relevance = if ($isRelevant) { 'relevant' } else { 'irrelevant' }
            expectations = [ordered]@{
                generatedSources = if ($isRelevant) { 'changed' } else { 'unchanged' }
                generatorDiagnostics = 'unchanged'
            }
        }
    }

    $json = $scenarioDocument | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText(
        (Join-Path $scenarioDirectory 'scenario.json'),
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}
