import SwiftUI
import AVKit
import MediaCore

@main
struct MediaServerApp: App {
    @StateObject private var model = AppModel()
    var body: some Scene { WindowGroup { RootView(model: model) } }
}

@MainActor
final class AppModel: ObservableObject {
    @Published var api: APIClient?
    @Published var identity: UserIdentity?
    @Published var error: String?
    @Published var busy = false
    func attach(_ client: APIClient) {
        api = client
        client.onExpired = { [weak self] in
            SessionVault.clear()
            self?.identity = nil
            self?.error = ClientError.expired.localizedDescription
        }
    }
    func restore() async {
        guard api == nil else { return }
        do {
            guard let (origin, cookies) = try SessionVault.restore() else { return }
            let client = APIClient(origin: origin)
            attach(client)
            for cookie in cookies { client.cookies.setCookie(cookie) }
            try await client.checkVersion()
            identity = try await client.get("auth/me")
        } catch { self.error = error.localizedDescription }
    }
    func login(origin: String, name: String, password: String) async {
        busy = true
        error = nil
        defer { busy = false }
        do {
            api?.clearCookies()
            SessionVault.clear()
            let client = APIClient(origin: try ServerOrigin.validate(origin))
            attach(client)
            let user = try await client.login(name: name, password: password)
            try SessionVault.save(origin: client.origin, cookies: client.cookies.cookies ?? [])
            identity = user
        } catch { self.error = error.localizedDescription }
    }
    func logout() async {
        do { try await api?.logout() }
        catch { self.error = "Signed out locally. Remote revocation could not be confirmed." }
        SessionVault.clear()
        identity = nil
        api = nil
    }
}

struct RootView: View {
    @ObservedObject var model: AppModel
    @State private var origin = ""
    @State private var name = ""
    @State private var password = ""
    var body: some View {
        Group {
            if let api = model.api, model.identity != nil {
                LibraryView(api: api, logout: { await model.logout() })
            } else {
                NavigationStack {
                    Form {
                        TextField("https://server.example", text: $origin)
                            .textInputAutocapitalization(.never).autocorrectionDisabled().keyboardType(.URL)
                        TextField("Username", text: $name).textInputAutocapitalization(.never).autocorrectionDisabled()
                        SecureField("Password", text: $password)
                        Button("Sign in") {
                            Task {
                                let secret = password
                                password = ""
                                await model.login(origin: origin, name: name, password: secret)
                            }
                        }.disabled(model.busy || password.isEmpty || name.isEmpty)
                        if model.busy { ProgressView() }
                        Text("Set up the server in its web interface first. Use trusted HTTPS for LAN and remote servers.").font(.footnote)
                    }.navigationTitle("Media Server")
                }
            }
        }
        .task { await model.restore() }
        .alert("Connection", isPresented: Binding(get: { model.error != nil }, set: { if !$0 { model.error = nil } })) {
            Button("OK") { model.error = nil }
        } message: { Text(model.error ?? "") }
    }
}

struct LibraryView: View {
    let api: APIClient
    let logout: () async -> Void
    @State private var media: [MediaSummary] = []
    @State private var error: String?
    @State private var loading = false
    @State private var hasMore = true
    var body: some View {
        NavigationStack {
            List {
                ForEach(media, id: \.id) { item in
                    NavigationLink(item.title) { DetailsView(api: api, id: item.id) }
                }
                if hasMore { Button("Load more") { Task { await load(reset: false) } }.disabled(loading) }
                if loading { ProgressView() }
                if let error { Text(error).foregroundStyle(.red) }
                if media.isEmpty && !loading { Text("No titles. Add a library using the server web interface.") }
            }
            .navigationTitle("Library")
            .toolbar { Button("Sign out") { Task { await logout() } } }
            .refreshable { await load(reset: true) }
            .task { if media.isEmpty { await load(reset: true) } }
        }
    }
    private func load(reset: Bool) async {
        guard !loading else { return }
        loading = true
        error = nil
        defer { loading = false }
        do {
            let offset = reset ? 0 : media.count
            let items: [MediaSummary] = try await api.get("media?offset=\(offset)&limit=100")
            media = reset ? items : media + items
            hasMore = items.count == 100
        } catch { self.error = error.localizedDescription }
    }
}

struct DetailsView: View {
    let api: APIClient
    let id: Int
    @State private var details: MediaDetails?
    @State private var error: String?
    @State private var playback: PlaybackModel?
    @State private var starting = false
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                if let details {
                    Text(details.title).font(.largeTitle)
                    Text(details.metadata?.description ?? "No description available.")
                    Text("\(details.container) · \(details.videoCodec) · \(details.audioCodec)")
                    Button("Play / Resume") {
                        starting = true
                        Task {
                            do {
                                let model = PlaybackModel(api: api, id: id)
                                try await model.start(details: details)
                                playback = model
                            } catch { self.error = error.localizedDescription }
                            starting = false
                        }
                    }.disabled(starting)
                } else { ProgressView() }
                if let error { Text(error).foregroundStyle(.red) }
            }.padding()
        }
        .task {
            do { details = try await api.get("media/\(id)") }
            catch { self.error = error.localizedDescription }
        }
        .fullScreenCover(item: $playback) { model in PlaybackView(model: model) }
    }
}

@MainActor
final class PlaybackModel: ObservableObject, Identifiable {
    let id: Int
    let api: APIClient
    let player = AVPlayer()
    @Published var error: String?
    private var writer: ProgressWriter?
    private var timer: Any?
    private var observation: NSKeyValueObservation?
    private var active = false
    init(api: APIClient, id: Int) { self.api = api; self.id = id }
    func start(details: MediaDetails) async throws {
        guard !Set(details.container.lowercased().split(separator: ",").map(String.init)).isDisjoint(with: ["mp4", "mov", "m4v"]),
              ["h264", "avc1"].contains(details.videoCodec.lowercased()),
              ["aac", "mp3", ""].contains(details.audioCodec.lowercased()) else {
            throw PlaybackFailure.unsupported
        }
        let state: ProgressState = try await api.get("media/\(id)/progress")
        writer = ProgressWriter(api: api, state: state)
        let asset = AVURLAsset(url: api.url("media/\(id)/stream"), options: [
            AVURLAssetHTTPCookiesKey: api.streamCookies(id: id),
            AVURLAssetPreferPreciseDurationAndTimingKey: true
        ])
        guard try await asset.load(.isPlayable) else { throw PlaybackFailure.unsupported }
        let item = AVPlayerItem(asset: asset)
        player.replaceCurrentItem(with: item)
        observation = item.observe(\.status, options: [.new]) { [weak self] item, _ in
            guard item.status == .failed else { return }
            Task { @MainActor [weak self] in
                self?.error = "Playback failed. Check the server connection and codec support, then reopen the title."
                self?.player.pause()
            }
        }
        let seconds = state.completed ? 0 : min(state.seconds, max(0, details.duration))
        await player.seek(to: CMTime(seconds: seconds, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        active = true
        timer = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 10, preferredTimescale: 1), queue: .main) { [weak self] _ in
            Task { @MainActor [weak self] in await self?.save() }
        }
        player.play()
    }
    func save() async {
        guard active, let writer else { return }
        let seconds = player.currentTime().seconds
        guard seconds.isFinite else { return }
        do { try await writer.save(seconds: seconds) }
        catch { self.error = error.localizedDescription; player.pause() }
    }
    func close() async {
        player.pause()
        await save()
        active = false
        if let timer { player.removeTimeObserver(timer); self.timer = nil }
        observation = nil
        player.replaceCurrentItem(with: nil)
    }
}

enum PlaybackFailure: LocalizedError {
    case unsupported
    var errorDescription: String? { "This iOS preview supports Direct Play H.264 MP4/MOV with AAC/MP3 audio. Convert this title on the server or use the web client." }
}

struct PlaybackView: View {
    @ObservedObject var model: PlaybackModel
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase
    var body: some View {
        VStack {
            VideoPlayer(player: model.player)
            if let error = model.error { Text(error).foregroundStyle(.red).padding() }
            Button("Close") { Task { await model.close(); dismiss() } }
        }
        .interactiveDismissDisabled()
        .onChange(of: scenePhase) { _, phase in
            if phase != .active {
                model.player.pause()
                Task { await model.save() }
            }
        }
        .onDisappear { Task { await model.close() } }
    }
}
