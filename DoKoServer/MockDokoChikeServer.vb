Imports System.Net
Imports System.Net.Sockets
Imports System.Linq

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

    Private Function Combine(header As Byte(), data As Byte(), footer As Byte()) As Byte()
        Dim total = header.Length + data.Length + footer.Length
        Dim buf(total - 1) As Byte
        Buffer.BlockCopy(header, 0, buf, 0, header.Length)
        Buffer.BlockCopy(data, 0, buf, header.Length, data.Length)
        Buffer.BlockCopy(footer, 0, buf, header.Length + data.Length, footer.Length)
        Return buf
    End Function

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
