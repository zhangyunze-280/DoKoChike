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

        ' 残りのアプリデータ部はひとまず 0x00 で埋める、なのでSUMを作る必要はない
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

' 判定要求通知コマンドに対するレスポンス（どこチケ側 → ABT）
Private Function BuildJudgmentResponse(reqHeader As Byte(),
                                       reqData As Byte(),
                                       reqFooter As Byte()) As Byte()

    ' ====== 定数定義 ======
    Const APP_TOTAL_LEN As Integer = 580               ' アプリ部総バイト数（SUM含む）
    Const APP_NO1_TO_NO11_LEN As Integer = 576         ' No.1～No.11 合計

    ' data 配列全体長 = CMD(1) + SUB(1) + LEN(2) + APP(580) = 584
    Dim data(APP_TOTAL_LEN + 3) As Byte                ' 0～583
    Const APP_BASE As Integer = 4                      ' = OFFSET_APP_DATA 相当

    ' --- CMD / SUB / Len(アプリ部長) ---
    data(0) = &HA4                                     ' コマンドコード
    data(1) = &HF0                                     ' サブコード（レスポンス）
    data(2) = CByte(APP_TOTAL_LEN And &HFF)            ' Len 下位
    data(3) = CByte((APP_TOTAL_LEN >> 8) And &HFF)     ' Len 上位

    ' ====== アプリ部内オフセット（No.1～No.12） ======
    Const OFF_PROC_DIR     As Integer = 0              ' No.1 処理方向             (1)
    Const OFF_RES_STATUS   As Integer = 1              ' No.2 応答ステータス       (1)
    Const OFF_DETAIL       As Integer = 2              ' No.3 詳細コード           (1)
    Const OFF_RESULT_INFO  As Integer = 3              ' No.4 判定結果情報         (132)
    Const OFF_VALID_FROM   As Integer = 135            ' No.5 有効開始日           (8)
    Const OFF_VALID_TO     As Integer = 143            ' No.6 有効終了日           (8)
    Const OFF_TICKET_NAME  As Integer = 151            ' No.7 券名称               (60)
    Const OFF_ERR_TITLE    As Integer = 211            ' No.8 エラータイトル       (40)
    Const OFF_ERR_TEXT     As Integer = 251            ' No.9 エラー内容テキスト   (240)
    Const OFF_ACTION_TEXT  As Integer = 491            ' No.10 エラー処理テキスト  (80)
    Const OFF_RESERVED     As Integer = 571            ' No.11 予備                (5)
    Const OFF_SUM          As Integer = 576            ' No.12 サム値              (4)

    Dim sjis As Encoding = Encoding.GetEncoding(932)

    ' ====== No.1: 処理方向 (1byte) ======
    ' リクエストのアプリ部先頭をそのままコピー（reqData は CMD/SUB/LEN/APP）
    Dim reqProcDir As Byte = reqData(4)
    data(APP_BASE + OFF_PROC_DIR) = reqProcDir

    ' ====== No.2: 応答ステータス (1byte) ======
    data(APP_BASE + OFF_RES_STATUS) = &H0      ' 0x00=OK

    ' ====== No.3: 詳細コード (1byte) ======
    data(APP_BASE + OFF_DETAIL) = &H0          ' とりあえず 0x00

    ' ====== No.4: 判定結果情報 (132byte) ======
    ' JudgeRequestLogic.QRcodeVerification のコメントに合わせて
    ' [0..5]   判定日時(6B)
    ' [6]      方向(1B)
    ' [7..18]  券番号(12B)
    ' [19]     媒体種別(1B)
    ' [20]     券種分類(1B)
    ' [21]     判定結果(1B)
    Dim resultInfo(131) As Byte

    ' 判定日時(6B) → 仮で 2025/12/06 15:15:00 相当っぽいダミー値
    resultInfo(0) = &H20
    resultInfo(1) = &H25
    resultInfo(2) = &H12
    resultInfo(3) = &H06
    resultInfo(4) = &H15
    resultInfo(5) = &H15

    ' 方向(1B) → リクエストの処理方向をそのままコピー
    resultInfo(6) = reqProcDir

    ' 券番号(12B) → 適当なダミー（ここでは "123456789012" 的なバイトを仮で入れる）
    For i = 0 To 11
        resultInfo(7 + i) = CByte(&H30 + ((i + 1) Mod 10)) ' '1'～'9','0','1',...
    Next

    ' 媒体種別(1B) → 0x02（QR）にする（ここが QR 判定ロジックのキー）
    resultInfo(19) = &H2

    ' 券種分類(1B) → 適当な 0x01
    resultInfo(20) = &H1

    ' 判定結果(1B) → 0x00（OK）。QRcodeVerification が「正常」とみなす条件。
    resultInfo(21) = &H0

    ' 残り [22..131] は 0 のままで OK
    Array.Copy(resultInfo, 0, data, APP_BASE + OFF_RESULT_INFO, 132)

    ' ====== No.5,6: 有効開始日・有効終了日 (各8byte, BCDっぽいダミー) ======
    Dim ymdFrom As Byte() = {&H20, &H25, &H12, &H06, &H0, &H0, &H0, &H0}
    Dim ymdTo   As Byte() = {&H20, &H25, &H12, &H31, &H0, &H0, &H0, &H0}
    Array.Copy(ymdFrom, 0, data, APP_BASE + OFF_VALID_FROM, 8)
    Array.Copy(ymdTo,   0, data, APP_BASE + OFF_VALID_TO,   8)

    ' ====== No.7: 券名称 (60byte, Shift-JIS) ======
    Dim ticketName As String = "テスト券名称"
    Dim ticketBytes = sjis.GetBytes(ticketName)
    Array.Clear(data, APP_BASE + OFF_TICKET_NAME, 60)
    Array.Copy(ticketBytes, 0, data, APP_BASE + OFF_TICKET_NAME,
               Math.Min(ticketBytes.Length, 60))

    ' ====== No.8: エラータイトル (40byte, Shift-JIS) ======
    Dim title As String = "正常処理"
    Dim titleBytes = sjis.GetBytes(title)
    Array.Clear(data, APP_BASE + OFF_ERR_TITLE, 40)
    Array.Copy(titleBytes, 0, data, APP_BASE + OFF_ERR_TITLE,
               Math.Min(titleBytes.Length, 40))

    ' ====== No.9: エラー内容テキスト (240byte, Shift-JIS) ======
    Dim errText As String = "処理完了しました。係員向け画面にそのまま表示される想定のテキストです。"
    Dim errBytes = sjis.GetBytes(errText)
    Array.Clear(data, APP_BASE + OFF_ERR_TEXT, 240)
    Array.Copy(errBytes, 0, data, APP_BASE + OFF_ERR_TEXT,
               Math.Min(errBytes.Length, 240))

    ' ====== No.10: エラー処理テキスト (80byte, Shift-JIS) ======
    Dim actionText As String = "特別な処置は不要です。"
    Dim actionBytes = sjis.GetBytes(actionText)
    Array.Clear(data, APP_BASE + OFF_ACTION_TEXT, 80)
    Array.Copy(actionBytes, 0, data, APP_BASE + OFF_ACTION_TEXT,
               Math.Min(actionBytes.Length, 80))

    ' ====== No.11: 予備 (5byte, 0x00 固定) ======
    Array.Clear(data, APP_BASE + OFF_RESERVED, 5)

    ' ====== ★ QRチェック用の媒体種別・判定結果をセット（JudgeRequestLogic と合わせる） ======
    Const REL_MEDIA As Integer = 19      ' JudgeRequestLogic.QRcodeVerification の定義と合わせる
    Const REL_RESULT As Integer = 21

    Dim mediaIndex As Integer = APP_BASE + REL_MEDIA
    Dim resultIndex As Integer = APP_BASE + REL_RESULT

    data(mediaIndex) = &H2   ' 媒体種別 = 0x02（QR）
    data(resultIndex) = &H0  ' 判定結果 = 0x00（OK）

    ' ====== No.12: SUM (4byte, UInt, Little Endian) ======
    ' アプリ内の 0 ～ 575（No.1～No.11）を足し算
    Dim sum As UInteger = 0UI
    For i = 0 To APP_NO1_TO_NO11_LEN - 1          ' 0 ～ 575
        sum += CUInt(data(APP_BASE + i))          ' 実際のインデックス 4 ～ 579
    Next
    Dim sumBytes = BitConverter.GetBytes(sum)
    Array.Copy(sumBytes, 0, data, APP_BASE + OFF_SUM, 4)  ' 4 + 576 = 580 ～ 583

    Console.WriteLine($"[Mock] SUM計算値={sum:X8}, 書き込みバイト={BitConverter.ToString(sumBytes)}")

    ' ====== ヘッダ更新（AppCount / AppSize） ======
    Dim header = CType(reqHeader.Clone(), Byte())

    ' アプリデータ数 = 1 固定
    Dim appCount As Integer = 1
    Dim appCountBytes = BitConverter.GetBytes(appCount)
    Array.Copy(appCountBytes, 0, header, 24, 4)         ' 24～27

    ' アプリデータサイズ = 580（仕様どおり）
    Dim appSizeBytes = BitConverter.GetBytes(APP_TOTAL_LEN)
    Array.Copy(appSizeBytes, 0, header, 28, 4)          ' 28～31

    ' ====== CRC32 計算＆フッタ生成 ======
    Dim crcTarget(header.Length + data.Length - 1) As Byte
    Buffer.BlockCopy(header, 0, crcTarget, 0, header.Length)
    Buffer.BlockCopy(data,   0, crcTarget, header.Length, data.Length)

    Dim crc As UInteger = ComputeCrc32(crcTarget)
    Dim footer(3) As Byte
    Buffer.BlockCopy(BitConverter.GetBytes(crc), 0, footer, 0, 4)

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
