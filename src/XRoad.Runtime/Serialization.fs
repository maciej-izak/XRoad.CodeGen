namespace XRoad.Runtime

open Emitter
open FSharp.Core
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Xml
open System.Xml.XPath
open XRoad.Runtime.Attributes

type internal XRoadFault(faultCode: string, faultString) =
    inherit Exception(faultString)
    member val FaultCode = faultCode with get
    member val FaultString = faultString with get

type internal XRoadResponse(response: WebResponse, methodMap: MethodMap) =
    let stream = new MemoryStream()

    let checkXRoadFault (stream: Stream) =
        let faultPath = "/*[local-name()='Envelope' and namespace-uri()='http://schemas.xmlsoap.org/soap/envelope/']/*[local-name()='Body' and namespace-uri()='http://schemas.xmlsoap.org/soap/envelope/']/*"
        let xpath =
            match methodMap.Protocol with
            | XRoadProtocol.Version40 -> faultPath
            | XRoadProtocol.Version20 -> sprintf "%s/keha" faultPath
            | _ -> sprintf "%s/response" faultPath
        stream.Position <- 0L
        use reader = XmlReader.Create(stream)
        let doc = XPathDocument(reader)
        let nav = doc.CreateNavigator()
        match nav.SelectSingleNode(sprintf "%s[faultCode|faultString]" xpath) with
        | null -> ()
        | node ->
            let faultCode = node.SelectSingleNode("./faultCode")
            let faultString = node.SelectSingleNode("./faultString")
            let nodeToString = Option.ofObj >> Option.map (fun x -> (x: XPathNavigator).InnerXml) >> Option.defaultValue ""
            raise(XRoadFault(faultCode |> nodeToString, faultString |> nodeToString))

    member __.RetrieveMessage() =
        use responseStream = response.GetResponseStream()
        responseStream.CopyTo(stream)

        let attachments = Dictionary<string, BinaryContent>()
        use content =
            stream.Position <- 0L
            let contentStream, atts = (stream, response) ||> MultipartMessage.read
            atts |> List.iter (fun content -> attachments.Add(content.ContentID, content))
            contentStream
        content |> checkXRoadFault
        content.Position <- 0L
        use reader = XmlReader.Create(content)
        if not (reader.MoveToElement(0, "Envelope", XmlNamespace.SoapEnv)) then
            failwith "Soap envelope element was not found in response message."
        if not (reader.MoveToElement(1, "Body", XmlNamespace.SoapEnv)) then
            failwith "Soap body element was not found in response message."
        let context = SerializerContext()
        attachments |> Seq.iter (fun kvp -> context.AddAttachment(kvp.Key, kvp.Value, false))
        if not (reader.MoveToElement(2, null, null)) then
            failwith "Soap message has empty payload in response."
        // TODO : validate response wrapper element
        match reader.LocalName, reader.NamespaceURI with
        | "Fault", XmlNamespace.SoapEnv -> failwithf "Request resulted an error: %s" (reader.ReadInnerXml())
        | _ -> methodMap.Deserializer.Invoke(reader, context)

    interface IXRoadResponse with
        member __.Save(outputStream: Stream) =
            let headers = response.Headers.ToByteArray()
            outputStream.Write(headers, 0, headers.Length)
            stream.Position <- 0L
            stream.CopyTo(outputStream)

    interface IDisposable with
        member __.Dispose() =
            stream.Dispose()
            (response :> IDisposable).Dispose()

type internal XRoadRequest(uri: Uri, methodMap: MethodMap, acceptedServerCertificate: X509Certificate, authenticationCertificates: ResizeArray<X509Certificate>) =
    let request =
        let request = WebRequest.Create(uri, Method="POST", ContentType="text/xml; charset=utf-8") |> unbox<HttpWebRequest>
        request.Headers.Set("SOAPAction", "")
        if acceptedServerCertificate |> isNull |> not then
            request.ServerCertificateValidationCallback <-
                (fun _ cert _ errors -> if errors = SslPolicyErrors.None then true else cert = acceptedServerCertificate)
        authenticationCertificates |> Seq.iter (request.ClientCertificates.Add >> ignore)
        request

    let addNamespace =
        let mutable i = 0
        (fun ns (writer: XmlWriter) ->
            if writer.LookupPrefix(ns) |> isNull then
                i <- i + 1
                writer.WriteAttributeString("xmlns", sprintf "ns%d" i, XmlNamespace.Xmlns, ns))

    let writeContent (stream: Stream) (content: Stream) =
        let buffer = Array.create 1000 0uy
        let rec writeChunk() =
            let bytesRead = content.Read(buffer, 0, 1000)
            stream.Write(buffer, 0, bytesRead)
            match bytesRead with 1000 -> writeChunk() | _ -> ()
        content.Position <- 0L
        writeChunk()

    let serializeMultipartMessage (context: SerializerContext) (serializeContent: Stream -> unit) (stream: Stream) =
        if context.Attachments.Count > 0 then
            use writer = new StreamWriter(stream, NewLine = "\r\n")
            let boundaryMarker = Guid.NewGuid().ToString()
            request.ContentType <-
                if context.IsMtomMessage then sprintf @"multipart/related; type=""application/xop+xml""; start=""<XML-%s>""; start-info=""text/xml""; boundary=""%s""" boundaryMarker boundaryMarker
                else sprintf @"multipart/related; type=""text/xml""; start=""<XML-%s>""; boundary=""%s""" boundaryMarker boundaryMarker
            request.Headers.Add("MIME-Version", "1.0")
            writer.WriteLine()
            writer.WriteLine("--{0}", boundaryMarker)
            if context.IsMtomMessage then writer.WriteLine(@"Content-Type: application/xop+xml; charset=UTF-8; type=""text/xml""")
            else writer.WriteLine("Content-Type: text/xml; charset=UTF-8")
            writer.WriteLine("Content-Transfer-Encoding: 8bit")
            writer.WriteLine("Content-ID: <XML-{0}>", boundaryMarker)
            writer.WriteLine()
            writer.Flush()
            stream |> serializeContent
            context.Attachments
            |> Seq.iter (fun kvp ->
                writer.WriteLine()
                writer.WriteLine("--{0}", boundaryMarker)
                writer.WriteLine("Content-Disposition: attachment; filename=notAnswering")
                writer.WriteLine("Content-Type: application/octet-stream")
                writer.WriteLine("Content-Transfer-Encoding: binary")
                writer.WriteLine("Content-ID: <{0}>", kvp.Key)
                writer.WriteLine()
                writer.Flush()
                use contentStream = kvp.Value.OpenStream()
                writeContent stream contentStream
                writer.WriteLine())
            writer.WriteLine("--{0}--", boundaryMarker)
        else stream |> serializeContent

    let serializeMessage (context: SerializerContext) (content: Stream) =
        serializeMultipartMessage context (fun s -> writeContent s content)

    let writeStringHeader req ns (writer: XmlWriter) value name =
        if req |> Array.exists ((=) name) || value |> String.IsNullOrEmpty |> not then
            writer.WriteStartElement(name, ns)
            if methodMap.Request.IsEncoded then
                writer.WriteStartAttribute("type", XmlNamespace.Xsi)
                writer.WriteQualifiedName("string", XmlNamespace.Xsd)
                writer.WriteEndAttribute()
            if String.IsNullOrEmpty(value) |> not then
                writer.WriteValue(value)
            writer.WriteEndElement()

    let writeBoolHeader (value: Nullable<bool>) name ns req (writer: XmlWriter) =
        if req |> Array.exists ((=) name) || value.HasValue then
            writer.WriteStartElement(name, ns)
            if methodMap.Request.IsEncoded then
                writer.WriteStartAttribute("type", XmlNamespace.Xsi)
                writer.WriteQualifiedName("boolean", XmlNamespace.Xsd)
                writer.WriteEndAttribute()
            if value.HasValue then
                writer.WriteValue(value)
            writer.WriteEndElement()

    let writeBase64Header (value: byte[]) name ns req (writer: XmlWriter) =
        let value = value |> Option.ofObj |> Option.defaultWith (fun _ -> [||])
        if req |> Array.exists ((=) name) || value |> Array.isEmpty |> not then
            writer.WriteStartElement(name, ns)
            if methodMap.Request.IsEncoded then
                writer.WriteStartAttribute("type", XmlNamespace.Xsi)
                writer.WriteQualifiedName("base64", XmlNamespace.Xsd)
                writer.WriteEndAttribute()
            if value |> Array.isEmpty |> not then
                writer.WriteValue(value)
            writer.WriteEndElement()

    let writeClientHeader (value: XRoadMemberIdentifier) req (writer: XmlWriter) =
        if req |> Array.exists ((=) "client") || not (value |> isNull) then
            writer.WriteStartElement("client", XmlNamespace.XRoad40)
            if not (value |> isNull) then
                writer.WriteStartAttribute("objectType", XmlNamespace.XRoad40Id)
                writer.WriteValue(if String.IsNullOrWhiteSpace(value.SubsystemCode) then "MEMBER" else "SUBSYSTEM")
                writer.WriteEndAttribute()
                writer.WriteStartElement("xRoadInstance", XmlNamespace.XRoad40Id)
                if not <| String.IsNullOrEmpty(value.XRoadInstance) then
                    writer.WriteValue(value.XRoadInstance)
                writer.WriteEndElement()
                writer.WriteStartElement("memberClass", XmlNamespace.XRoad40Id)
                if not <| String.IsNullOrEmpty(value.MemberClass) then
                    writer.WriteValue(value.MemberClass)
                writer.WriteEndElement()
                writer.WriteStartElement("memberCode", XmlNamespace.XRoad40Id)
                if not <| String.IsNullOrEmpty(value.MemberCode) then
                    writer.WriteValue(value.MemberCode)
                writer.WriteEndElement()
                if String.IsNullOrWhiteSpace(value.SubsystemCode) |> not then
                    writer.WriteStartElement("subsystemCode", XmlNamespace.XRoad40Id)
                    writer.WriteValue(value.SubsystemCode)
                    writer.WriteEndElement()
            writer.WriteEndElement()

    let getServiceName producerName =
        let serviceName = match methodMap.ServiceVersion with
                          | Some(version) -> sprintf "%s.%s" methodMap.ServiceCode version
                          | None -> methodMap.ServiceCode
        if producerName |> String.IsNullOrEmpty then serviceName
        else sprintf "%s.%s" producerName serviceName

    let writeServiceHeader (value: XRoadMemberIdentifier) req (writer: XmlWriter) =
        if req |> Array.exists ((=) "service") || not (value |> isNull) then
            writer.WriteStartElement("service", XmlNamespace.XRoad40)
            if not (value |> isNull) then
                writer.WriteStartAttribute("objectType", XmlNamespace.XRoad40Id)
                writer.WriteValue("SERVICE")
                writer.WriteEndAttribute()
                writer.WriteStartElement("xRoadInstance", XmlNamespace.XRoad40Id)
                if not <| String.IsNullOrEmpty(value.XRoadInstance) then
                    writer.WriteValue(value.XRoadInstance)
                writer.WriteEndElement()
                writer.WriteStartElement("memberClass", XmlNamespace.XRoad40Id)
                if not <| String.IsNullOrEmpty(value.MemberClass) then
                    writer.WriteValue(value.MemberClass)
                writer.WriteEndElement()
                writer.WriteStartElement("memberCode", XmlNamespace.XRoad40Id)
                if not <| String.IsNullOrEmpty(value.MemberCode) then
                    writer.WriteValue(value.MemberCode)
                writer.WriteEndElement()
                if String.IsNullOrWhiteSpace(value.SubsystemCode) |> not then
                    writer.WriteStartElement("subsystemCode", XmlNamespace.XRoad40Id)
                    writer.WriteValue(value.SubsystemCode)
                    writer.WriteEndElement()
                writer.WriteStartElement("serviceCode", XmlNamespace.XRoad40Id)
                if not <| String.IsNullOrEmpty(methodMap.ServiceCode) then
                    writer.WriteValue(methodMap.ServiceCode)
                writer.WriteEndElement()
                match methodMap.ServiceVersion with
                | Some(version) ->
                    writer.WriteStartElement("serviceVersion", XmlNamespace.XRoad40Id)
                    writer.WriteValue(version)
                    writer.WriteEndElement()
                | None -> ()
            writer.WriteEndElement()

    let writeXRoadHeader (id: string) (header: AbstractXRoadHeader) (writer: XmlWriter) =
        methodMap.RequiredHeaders.Keys |> Seq.iter (fun ns -> writer |> addNamespace ns)
        let headerNamespace = XRoadHelper.protocolNamespace methodMap.Protocol
        let requiredHeaders = match methodMap.RequiredHeaders.TryGetValue(headerNamespace) with true, xs -> xs | _ -> [||]
        let writeStringHeader' = writeStringHeader requiredHeaders headerNamespace writer
        match header with
        | :? XRoadRpcHeader as header ->
            writeStringHeader' header.Asutus "asutus"
            writeStringHeader' header.Andmekogu "andmekogu"
            writeStringHeader' header.Isikukood "isikukood"
            writeStringHeader' header.Ametnik "ametnik"
            writeStringHeader' id "id"
            writeStringHeader' (getServiceName header.Andmekogu) "nimi"
            writeStringHeader' header.Toimik "toimik"
            writeStringHeader' header.Allasutus "allasutus"
            writeStringHeader' header.Amet "amet"
            writeStringHeader' header.AmetnikNimi "ametniknimi"
            writer |> writeBoolHeader header.Asynkroonne "asynkroonne" headerNamespace requiredHeaders
            writeStringHeader' header.Autentija "autentija"
            writeStringHeader' header.Makstud "makstud"
            writeStringHeader' header.Salastada "salastada"
            writer |> writeBase64Header header.SalastadaSertifikaadiga "salastada_sertifikaadiga" headerNamespace requiredHeaders
            writeStringHeader' header.Salastatud "salastatud"
            writeStringHeader' header.SalastatudSertifikaadiga "salastatud_sertifikaadiga"
        | :? XRoadDocHeader as header ->
            writeStringHeader' header.Consumer "consumer"
            writeStringHeader' header.Producer "producer"
            writeStringHeader' header.UserId "userId"
            writeStringHeader' id "id"
            writeStringHeader' (getServiceName header.Producer) "service"
            writeStringHeader' header.Issue "issue"
            writeStringHeader' header.Unit "unit"
            writeStringHeader' header.Position "position"
            writeStringHeader' header.UserName "userName"
            writer |> writeBoolHeader header.Async "async" headerNamespace requiredHeaders
            writeStringHeader' header.Authenticator "authenticator"
            writeStringHeader' header.Paid "paid"
            writeStringHeader' header.Encrypt "encrypt"
            writer |> writeBase64Header header.EncryptCert "encryptCert" headerNamespace requiredHeaders
            writeStringHeader' header.Encrypted "encrypted"
            writeStringHeader' header.EncryptedCert "encryptedCert"
        | :? XRoadHeader as header ->
            if writer.LookupPrefix(XmlNamespace.XRoad40Id) |> isNull then
                writer.WriteAttributeString("xmlns", "id", XmlNamespace.Xmlns, XmlNamespace.XRoad40Id)
            writer |> writeClientHeader header.Client requiredHeaders
            writer |> writeServiceHeader header.Producer requiredHeaders
            writeStringHeader' id "id"
            writeStringHeader' header.UserId "userId"
            writeStringHeader' header.Issue "issue"
            writeStringHeader' header.ProtocolVersion "protocolVersion"
        | _ -> failwithf "Unexpected X-Road header type `%s`." (header.GetType().FullName)
        header.Unresolved |> Seq.iter (fun e -> e.WriteTo(writer))

    let stream = new MemoryStream() :> Stream

    member __.CreateMessage(requestId, header, args) =
        use content = new MemoryStream()
        use sw = new StreamWriter(content)
        let context = SerializerContext(IsMultipart = methodMap.Request.IsMultipart)
        use writer = XmlWriter.Create(sw)
        writer.WriteStartDocument()
        writer.WriteStartElement("soapenv", "Envelope", XmlNamespace.SoapEnv)
        writer.WriteAttributeString("xmlns", "xsi", XmlNamespace.Xmlns, XmlNamespace.Xsi)
        writer.WriteAttributeString("xmlns", XRoadHelper.protocolPrefix methodMap.Protocol, XmlNamespace.Xmlns, XRoadHelper.protocolNamespace methodMap.Protocol)
        methodMap.Namespaces |> Seq.iteri (fun i ns -> writer.WriteAttributeString("xmlns", sprintf "ns%d" i, XmlNamespace.Xmlns, ns))
        methodMap.Request.Accessor |> Option.iter (fun acc -> writer.WriteAttributeString("xmlns", "acc", XmlNamespace.Xmlns, acc.Namespace))
        if methodMap.Request.IsEncoded then
            writer.WriteAttributeString("xmlns", "xsd", XmlNamespace.Xmlns, XmlNamespace.Xsd)
            writer.WriteAttributeString("encodingStyle", XmlNamespace.SoapEnv, XmlNamespace.SoapEnc)
        writer.WriteStartElement("Header", XmlNamespace.SoapEnv)
        writer |> writeXRoadHeader requestId header
        writer.WriteEndElement()
        writer.WriteStartElement("Body", XmlNamespace.SoapEnv)
        methodMap.Serializer.Invoke(writer, args, context)
        writer.WriteEndElement()
        writer.WriteEndDocument()
        writer.Flush()
        (content, stream) ||> serializeMessage context

    member __.SendMessage() =
        stream.Position <- 0L
        use networkStream = request.GetRequestStream()
        stream.CopyTo(networkStream)

    member __.GetResponse(methodMap: MethodMap) =
        new XRoadResponse(request.GetResponse(), methodMap)

    interface IXRoadRequest with
        member __.Save(outputStream: Stream) =
            let headers = request.Headers.ToByteArray()
            outputStream.Write(headers, 0, headers.Length)
            stream.Position <- 0L
            stream.CopyTo(outputStream)

    interface IDisposable with
        member __.Dispose() =
            stream.Dispose()

type public XRoadUtil =
    static member MakeServiceCall(endpoint: AbstractEndpointDeclaration, methodName: string, header: AbstractXRoadHeader, args: obj[]) =
        let serviceMethod = endpoint.GetType().GetMethod(methodName)
        let serviceMethodMap = getMethodMap serviceMethod
        use request = new XRoadRequest(endpoint.Uri, serviceMethodMap, endpoint.AcceptedServerCertificate, endpoint.AuthenticationCertificates)
        let requestId = if String.IsNullOrWhiteSpace(header.Id) then XRoadHelper.getUuid() else header.Id
        request.CreateMessage(requestId, header, args)
        endpoint.TriggerRequestReady(RequestReadyEventArgs(request, header, requestId, serviceMethodMap.ServiceCode, serviceMethodMap.ServiceVersion |> Option.defaultValue ""))
        request.SendMessage()
        use response = request.GetResponse(serviceMethodMap)
        let result = response.RetrieveMessage()
        endpoint.TriggerResponseReady(ResponseReadyEventArgs(response, header, requestId, serviceMethodMap.ServiceCode, serviceMethodMap.ServiceVersion |> Option.defaultValue ""))
        result
