# SnipDrag

Aplicativo Windows Forms que fica no tray, detecta imagens copiadas para o clipboard pelo Snipping Tool (`Win+Shift+S`), salva a captura como PNG e exibe um thumbnail temporario perto do relogio.

## Como usar

```powershell
dotnet run
```

Depois de iniciar, use `Win+Shift+S` e finalize a captura. O thumbnail aparece no canto inferior direito por 10 segundos. Arraste o thumbnail para outra aplicacao para enviar o arquivo como `FileDrop` e tambem como texto com o caminho absoluto da imagem.

Clique com o botao esquerdo no icone do tray para abrir as configuracoes. Clique com o botao direito para abrir o menu.

## Configuracoes

- Pasta de destino: por padrao, `%TEMP%\SnipDrag`.
- Tempo do thumbnail: por padrao, 10 segundos.
- Formato do path ao arrastar:
  - Automatico: tenta usar `/mnt/c/...` quando detectar uma janela WSL sob o cursor.
  - Windows: envia `C:\...`.
  - WSL: envia `/mnt/c/...`.
  - Windows + WSL: envia os dois caminhos em linhas separadas.
- Iniciar com o Windows: grava/remove a entrada `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SnipDrag`.
- Excluir prints antigos ao iniciar: remove arquivos `snip-*.png` da pasta configurada quando o app abre.

O modo automatico para WSL e de melhor esforco: ele verifica processo e titulo da janela sob o cursor durante o drag. Se nao houver sinal claro de WSL, o app mantem o caminho Windows.

## Publicar localmente

```powershell
dotnet publish .\SnipDrag.csproj -c Release -r win-x64 --self-contained false
```

O executavel fica em:

```text
bin\Release\net10.0-windows\win-x64\publish\SnipDrag.exe
```

## Releases no GitHub

O workflow `.github/workflows/release.yml` gera uma release automaticamente a cada push em `main` ou `master` quando houver mudancas em `SnipDrag/**` ou no proprio workflow.

Cada release publica:

- `SnipDrag-<versao>-win-x64-portable.zip`: versao portatil.
- `SnipDrag-Setup-<versao>.exe`: instalador Windows per-user.

Os artefatos de release sao `self-contained`, entao incluem o runtime necessario para executar o app.

Tambem e possivel disparar a release manualmente pelo botao `Run workflow` no GitHub Actions.
