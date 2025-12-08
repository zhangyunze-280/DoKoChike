Imports System.Net
Imports System.Net.Sockets
Imports System.Linq
Imports System.Text

Module MockDokoChikeServer

    ' ★★★ ABT 側の UdpReceiver が待ち受けているポートに合わせる ★★★
    Private Const AbtReceivePort As Integer = 50001   ' ← ここの値を ABT の設定と合わせる

    Sub Main()
        Dim server As New UdpClient(51000) ' どこチケ側固定ポート
        Console.WriteLine("Mockどこチケサーバ起動 (port=51000)")

        While True
            Dim remoteEP As New IPEndPoint(IPAddress.Any, 0)
            Dim rcv As Byte()

            Try
                rcv = server.Receive(remoteEP)
            Catch ex As SocketException
                Console.WriteLine($"[WARN] Receive error: {ex.Message} (Code={ex.ErrorCode})")
                Continue While
            End Try

            Console.WriteLine($"受信 {rcv.Length} bytes from {remoteEP}")

            If rcv.Length < 32 + 4 Then
                Console.WriteLine("短すぎるので無視")
                Continue While
            End If

            Dim header = rcv.Take(32).ToArray()
            Dim footer = rcv.Skip(rcv.Length - 4).ToArray()
            Dim data = rcv.Skip(32).Take(rcv.Length - 32 - 4).ToArray()

            Dim cmd As Byte = data(0)
            Dim subc As Byte = data(1)

            Console.WriteLine("App=" & BitConverter.ToString(data))
            Console.WriteLine($"cmd={cmd:X2}, sub={subc:X2}")

            Dim respBytes As Byte() = Nothing

            Select Case True
                Case (cmd = &HC3 AndAlso subc = &H0)
                    respBytes = BuildAuthResponse(header, data, footer)

    ' ★ D1 / 00 （ヘルス要求）に応答する
                Case (cmd = &HD1 AndAlso subc = &H0)
                    respBytes = BuildHealthResponse(header)

                ' ★ 判定要求通知コマンド (0xA4 / 0x00)  ★
                Case (cmd = &HA4 AndAlso subc = &H0)
                    respBytes = BuildJudgmentResponse(header, data, footer)

                Case Else
                    Console.WriteLine($"未知コマンド: cmd={cmd:X2}, sub={subc:X2}")
            End Select

            If respBytes IsNot Nothing Then
                ' ★★ 重要：remoteEP ではなく、AbtReceivePort に固定 ★★
                Dim abtEP As New IPEndPoint(IPAddress.Loopback, AbtReceivePort)
                server.Send(respBytes, respBytes.Length, abtEP)
                Console.WriteLine($"→ 応答パケット送信 to {abtEP}")
            End If
        End While
    End Sub

    ' --- 以下はさっきのまま ---
    ' 認証データのレスポンス
    Private Function BuildAuthResponse(reqHeader As Byte(),
                                   reqData As Byte(),
                                   reqFooter As Byte()) As Byte()

        Dim header = CType(reqHeader.Clone(), Byte())

        Const APP_LEN As Integer = 512 ' 仕様上の応答アプリ長
        Dim data(APP_LEN + 3) As Byte  ' cmd(1)+sub(1)+Len(2)+App(512)=516

        ' ---- 先頭4バイト（コマンド／サブコード／Len） ----
        data(0) = &HC3        ' コマンドコード
        data(1) = &HF0        ' サブコード（レスポンス）
        data(2) = CByte(APP_LEN And &HFF)        ' Len 下位
        data(3) = CByte((APP_LEN >> 8) And &HFF) ' Len 上位

        Dim appStart As Integer = 4

        ' 応答ステータス = 0x00 (OK)
        data(appStart + 0) = &H0

        ' 詳細コード = 0x00 (初期値)
        data(appStart + 1) = &H0

        ' 残りのアプリデータ部はひとまず 0x00 で埋める
        For i = appStart + 2 To appStart + APP_LEN - 1
            data(i) = &H0
        Next

        ' ---- ヘッダ側のアプリデータ数/サイズも修正 ----
        ' アプリデータ数 = 1 固定
        Dim appCount As Integer = 1
        Dim appCountBytes = BitConverter.GetBytes(appCount)
        Array.Copy(appCountBytes, 0, header, 24, 4)   ' offset 24〜27

        ' アプリデータサイズ = APP_LEN(=512) でひとまずOK
        Dim appSize As Integer = APP_LEN
        Dim appSizeBytes = BitConverter.GetBytes(appSize)
        Array.Copy(appSizeBytes, 0, header, 28, 4)    ' offset 28〜31

        ' ---- CRC 計算＆フッタ生成 ----
        Dim crcTarget(header.Length + data.Length - 1) As Byte
        Array.Copy(header, 0, crcTarget, 0, header.Length)
        Array.Copy(data, 0, crcTarget, header.Length, data.Length)

        Dim crc As UInteger = ComputeCrc32(crcTarget)
        Dim crcBytes As Byte() = BitConverter.GetBytes(crc)

        Dim footer(3) As Byte
        Array.Copy(crcBytes, 0, footer, 0, 4)

        Return Combine(header, data, footer)
    End Function

    '死活のレスポンス
    Private Function BuildHealthResponse(reqHeader As Byte()) As Byte()

        ' ヘッダは基本そのまま流用（Seq やブロックNoなどはそのままでOK）
        Dim header = CType(reqHeader.Clone(), Byte())

        ' data: D1 / F0 / Len=0 (Appなし)
        Dim data(3) As Byte
        data(0) = &HD1          ' CMD
        data(1) = &HF0          ' SUB（レスポンス）
        data(2) = 0             ' Len LSB = 0
        data(3) = 0             ' Len MSB = 0

        ' header + data で CRC32 を取り直す
        Dim body(header.Length + data.Length - 1) As Byte
        Buffer.BlockCopy(header, 0, body, 0, header.Length)
        Buffer.BlockCopy(data, 0, body, header.Length, data.Length)

        Dim crc As UInteger = ComputeCrc32(body)
        Dim footer(3) As Byte
        Buffer.BlockCopy(BitConverter.GetBytes(crc), 0, footer, 0, 4)

        Return Combine(header, data, footer)
    End Function

    ' 判定要求通知コマンドに対するレスポンス（サーバー→ABT）
    Private Function BuildJudgmentResponse(reqHeader As Byte(),reqData As Byte(),reqFooter As Byte()) As Byte()

        ' ヘッダを複製して、応答ヘッダとして流用
        Dim header = CType(reqHeader.Clone(), Byte())

        ' 判定要求応答のアプリデータ長は 580 byte
        Const APP_DATA_ONLY_LEN As Integer = 580 
        ' データ部（No.1～4）全体のサイズ
        Const DATA_PART_TOTAL_LEN As Integer = APP_DATA_ONLY_LEN + 4

        ’アプリデータサイズ
        Dim appSize As Integer = DATA_PART_TOTAL_LEN  ' ← 584 を設定
        Dim appSizeBytes = BitConverter.GetBytes(appSize)
        Array.Copy(appSizeBytes, 0, header, 28, 4)

        ' データ部全体のサイズ: CMD(1) + SUB(1) + Len(2) + App(580) = 584 byte
        Dim data(APP_DATA_ONLY_LEN + 3) As Byte 

        ' ---- 1. 先頭4バイト（コマンド／サブコード／Len） ----
        data(0) = &HA4                      ' コマンドコード: 0xA4
        data(1) = &HF0                      ' サブコード（レスポンス）: 0xF0
        data(2) = CByte(APP_DATA_ONLY_LEN And &HFF)   ' Len (580) 下位
        data(3) = CByte((APP_DATA_ONLY_LEN >> 8) And &HFF) ' Len (580) 上位

        Dim appStart As Integer = 4
        Dim sjisEncoding As Encoding = Encoding.GetEncoding(932)

        ' ---- 2. アプリケーションデータ部 (580バイト) の構築 ----
    
        ' 応答内容の主要なオフセット (Image_6f4558.png を参照)
        Const OFFSET_PROC_DIR As Integer = 0
        Const OFFSET_RES_STATUS As Integer = 1
        Const OFFSET_DETAIL_CODE As Integer = 2
        Const OFFSET_RESULT_INFO As Integer = 3
        Const OFFSET_START_DATE As Integer = 135  ' 1 + 1 + 1 + 132 = 135
        Const OFFSET_TITLE As Integer = 219     ' (例示のため、簡略化してオフセットを使用)
        Const OFFSET_CONTENT As Integer = 259
        Const OFFSET_ACTION As Integer = 499

        ' a) 処理方向 (1 byte)
        '    ABTから送られてきた判定要求通知のデータ部からコピー
        data(appStart + OFFSET_PROC_DIR) = reqData(4) ' reqDataはCMD(1)+SUB(1)+Len(2)+App(580)のデータ部
    
        ' b) 応答ステータス (1 byte) - 0x00:OKを返す
        data(appStart + OFFSET_RES_STATUS) = &H0 
    
        ' c) 詳細コード (1 byte) - 0x00:初期値
        data(appStart + OFFSET_DETAIL_CODE) = &H0 

        ' d) 判定結果情報 (132 bytes) - ダミーデータで埋める
        Dim dummyResultInfo As String = New String("R"c, 66) ' 全角66文字 = 132バイト
        sjisEncoding.GetBytes(dummyResultInfo).CopyTo(data, appStart + OFFSET_RESULT_INFO)

        ' e) 有効開始日 (8 bytes) - BCDで日付 "20251206" を設定
        ' BCD(20 25 12 06)
        data(appStart + OFFSET_START_DATE + 0) = &H20
        data(appStart + OFFSET_START_DATE + 1) = &H25
        data(appStart + OFFSET_START_DATE + 2) = &H12
        data(appStart + OFFSET_START_DATE + 3) = &H06
    
        ' f) エラータイトル (40 bytes, Shift-JIS) - 全角文字のテスト
        Dim titleBytes As Byte() = sjisEncoding.GetBytes("正常処理")
        titleBytes.CopyTo(data, appStart + OFFSET_TITLE)
    
        ' g) エラー内容テキスト (240 bytes, Shift-JIS) - 全角文字のテスト
        Dim contentBytes As Byte() = sjisEncoding.GetBytes("改札機 (係員向け) 画面にそのまま表示するテキスト。処理完了。")
        contentBytes.CopyTo(data, appStart + OFFSET_CONTENT)
    
        ' h) エラー処置テキスト (80 bytes, Shift-JIS) - 全角文字のテスト
        Dim actionBytes As Byte() = sjisEncoding.GetBytes("エラー処理テキスト")
        actionBytes.CopyTo(data, appStart + OFFSET_ACTION)

        ' 残りのアプリデータ部（すべてを埋める必要がない場合）は 0x00 で埋める（既定で初期化されるため省略可）
    
        ' ---- 3. ヘッダ側のアプリデータ数/サイズも修正 ----
        ' アプリデータ数 = 1 固定
        Dim appCount As Integer = 1
        Dim appCountBytes = BitConverter.GetBytes(appCount)
        Array.Copy(appCountBytes, 0, header, 24, 4)  

        Dim sumTarget(575) As Byte
        Array.Copy(data, appStart, sumTarget, 0, 576)

        Dim calculatedSum As UInteger = ComputeSum(sumTarget)

        Dim sumBytes = BitConverter.GetBytes(calculatedSum)
        Array.Copy(sumBytes, 0, data, appStart + 576, 4) ' 580バイト目のアプリデータ部に格納

        ' ---- 4. CRC 計算＆フッタ生成 ----
        Dim crcTarget(header.Length + data.Length - 1) As Byte
        Buffer.BlockCopy(header, 0, crcTarget, 0, header.Length)
        Buffer.BlockCopy(data, 0, crcTarget, header.Length, data.Length)

        Dim crc As UInteger = ComputeCrc32(crcTarget)
        Dim crcBytes As Byte() = BitConverter.GetBytes(crc)

        Dim footer(3) As Byte
        Buffer.BlockCopy(crcBytes, 0, footer, 0, 4)

        ' ヘッダ + データ + フッタ を結合して返す
        Return Combine(header, data, footer)
    End Function

    'ヘッダ、データ、フッタ結合
    Private Function Combine(header As Byte(), data As Byte(), footer As Byte()) As Byte()
        Dim total = header.Length + data.Length + footer.Length
        Dim buf(total - 1) As Byte
        Buffer.BlockCopy(header, 0, buf, 0, header.Length)
        Buffer.BlockCopy(data, 0, buf, header.Length, data.Length)
        Buffer.BlockCopy(footer, 0, buf, header.Length + data.Length, footer.Length)
        Return buf
    End Function

    Private Function ComputeSum(data() As Byte) As UInteger
        Dim total As UInteger = 0
        For Each b In data
            total += CUInt(b)
        Next
        Return total
    End Function

    ’CRC計算
    Private Function ComputeCrc32(data As Byte()) As UInteger
        Const poly As UInteger = &HEDB88320UI
        Static tbl As UInteger() = Nothing
        If tbl Is Nothing Then
            ReDim tbl(255)
            For i = 0 To 255
                Dim c As UInteger = CUInt(i)
                For j = 0 To 7
                    If (c And 1UI) <> 0UI Then
                        c = (c >> 1) Xor poly
                    Else
                        c >>= 1
                    End If
                Next
                tbl(i) = c
            Next
        End If

        Dim crc As UInteger = &HFFFFFFFFUI
        For Each b In data
            Dim idx = CInt((crc Xor b) And &HFFUI)
            crc = (crc >> 8) Xor tbl(idx)
        Next
        Return Not crc
    End Function


End Module
