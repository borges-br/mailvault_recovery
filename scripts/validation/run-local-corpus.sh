#!/bin/bash
# run-local-corpus.sh
# Script de automação do laboratório de validação contra corpus local

set -e

corpusDir="./.local-corpus"
if [ ! -d "$corpusDir" ]; then
    echo "[!] Laboratório local não configurado. Por favor, crie a pasta '.local-corpus/'"
    exit 1
fi

echo "================================================================================"
echo "             MailVault Recovery — Laboratório de Validação Manual               "
echo "================================================================================"

# Find ost/pst/mbox files including Thunderbird extensionless mboxes in thunderbird/mbox/
files=$(find "$corpusDir" -type f \( -name "*.ost" -o -name "*.pst" -o -name "*.mbox" -o \( -path "*/thunderbird/mbox/*" -not -name "*.msf" -not -name "*.sbd" \) \))

if [ -z "$files" ]; then
    echo "[!] Nenhum arquivo OST, PST ou MBOX localizado em $corpusDir. Carregue mídias reais para testar."
    exit 0
fi

timestamp=$(date +"%Y%m%d-%H%M%S")
runDir="$corpusDir/results/runs/$timestamp"
mkdir -p "$runDir"

cliDll="./src/MailVault.Cli/bin/Debug/net10.0/MailVault.Cli.dll"
echo "[*] Compilando solução para garantir binários atualizados..."
dotnet build ./MailVault.sln -c Debug > /dev/null

# Initialize summary fields
resultsJson="[]"
totalSize=0
typesProcessed=()

# Helper for timing in milliseconds
get_time_ms() {
    echo $(date +%s%3N | cut -b1-13)
}

for file in $files; do
    fileName=$(basename "$file")
    fileSizeBytes=$(wc -c < "$file" | tr -d ' ')
    totalSize=$((totalSize + fileSizeBytes))
    
    fileExt="${fileName##*.}"
    if [ "$fileExt" = "$fileName" ]; then
        fileType="mbox"
    else
        fileType=$(echo "$fileExt" | tr 'A-Z' 'a-z')
    fi
    
    echo -e "\n[*] Processando mídia real: $fileName ($fileType, $((fileSizeBytes / 1024 / 1024)) MB)..."
    
    caseId="CASE-VAL-${fileName^^}-$timestamp"
    caseId=$(echo "$caseId" | tr ' ' '_')
    caseFolder="./mailvault-cases/$caseId"
    exportEmlDir="$caseFolder/exports-eml"
    exportMboxDir="$caseFolder/exports-mbox"
    
    # 1. Index
    echo "  -> Indexando no case.db..."
    start_t=$(get_time_ms)
    dotnet "$cliDll" index "$file" --case-id "$caseId" > /dev/null
    end_t=$(get_time_ms)
    index_ms=$((end_t - start_t))
    
    # 2. Stats
    echo "  -> Extraindo estatísticas..."
    start_t=$(get_time_ms)
    dotnet "$cliDll" stats "$caseFolder" > /dev/null
    end_t=$(get_time_ms)
    stats_ms=$((end_t - start_t))
    
    # 3. Search
    echo "  -> Testando busca rápida..."
    start_t=$(get_time_ms)
    dotnet "$cliDll" search "$caseFolder" --query "Wagner" > /dev/null
    end_t=$(get_time_ms)
    search_ms=$((end_t - start_t))
    
    # 4. Export EML
    echo "  -> Exportando para EML..."
    start_t=$(get_time_ms)
    dotnet "$cliDll" export "$caseFolder" --format eml --out "$exportEmlDir" --include-attachments --extract-attachments --overwrite > /dev/null
    end_t=$(get_time_ms)
    export_eml_ms=$((end_t - start_t))
    
    # 5. Export MBOX
    echo "  -> Exportando para MBOX..."
    start_t=$(get_time_ms)
    dotnet "$cliDll" export "$caseFolder" --format mbox --out "$exportMboxDir" --overwrite > /dev/null
    end_t=$(get_time_ms)
    export_mbox_ms=$((end_t - start_t))
    
    # 6. Validate EML
    echo "  -> Validando exportação EML..."
    start_t=$(get_time_ms)
    dotnet "$cliDll" validate "$caseFolder" --export-folder "$exportEmlDir" --format eml --strict --out "$runDir" > /dev/null
    end_t=$(get_time_ms)
    validate_ms=$((end_t - start_t))
    
    origReport="$runDir/validation-report.json"
    destReport="$runDir/validation-report-${fileName// /_}.json"
    
    indexedMsgs=0
    exportedMsgs=0
    indexedAtts=0
    exportedAtts=0
    warnings=0
    errors=0
    status="Failed"
    
    if [ -f "$origReport" ]; then
        # Mask user paths in individual report file
        sed -i 's|'"$HOME"'|C:\\Users\\<USER>|g' "$origReport"
        sed -i 's|/home/[a-zA-Z0-9_-]*|C:\\Users\\<USER>|g' "$origReport"
        
        # Read metrics from JSON using basic grep/sed for portability (without jq)
        indexedMsgs=$(grep -o '"indexed_messages": *[0-9]*' "$origReport" | grep -o '[0-9]*')
        exportedMsgs=$(grep -o '"exported_messages": *[0-9]*' "$origReport" | grep -o '[0-9]*')
        indexedAtts=$(grep -o '"indexed_attachments": *[0-9]*' "$origReport" | grep -o '[0-9]*')
        exportedAtts=$(grep -o '"exported_attachments": *[0-9]*' "$origReport" | grep -o '[0-9]*')
        warnings=$(grep -o '"warning_count": *[0-9]*' "$origReport" | grep -o '[0-9]*')
        errors=$(grep -o '"error_count": *[0-9]*' "$origReport" | grep -o '[0-9]*')
        status=$(grep -o '"status": *"[A-Za-z]*"' "$origReport" | cut -d'"' -f4)
        
        mv "$origReport" "$destReport"
    fi
    
    # Track types
    if [[ ! " ${typesProcessed[@]} " =~ " ${fileType} " ]]; then
        typesProcessed+=("$fileType")
    fi
    
    # Append to results
    echo "  [x] Mídia processada e validada com status: $status"
done

# Save a simple summary.json
cat <<EOF > "$runDir/summary.json"
{
  "run_timestamp": "$timestamp",
  "files_processed": $(echo "$files" | wc -w),
  "total_size_bytes": $totalSize,
  "status": "Completed"
}
EOF

# Save a simple summary.md
cat <<EOF > "$runDir/summary.md"
# Relatório Consolidado de Validação de Corpus Real (Unix)

**Timestamp do Run:** $timestamp  
**Tamanho Total Processado:** $((totalSize / 1024 / 1024)) MB  

*Nota: Os relatórios detalhados foram salvos individualmente em $runDir/validation-report-*.json*
EOF

echo -e "\n================================================================================"
echo "[x] Rodada concluída com sucesso. Relatórios salvos em: $runDir"
echo "================================================================================"
