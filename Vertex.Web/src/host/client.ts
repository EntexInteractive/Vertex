// The typed client for the desktop app's local API (Vertex.Gui). Types come from host-api.d.ts, which
// `npm run gen:host` generates from host-api.json, which building Vertex.Gui writes. Don't edit either by hand.
import createClient from 'openapi-fetch'
import type {components, paths} from './host-api'

export type HostSchemas = components['schemas']

const TOKEN_HEADER = 'X-Vertex-Token'
const TOKEN_KEY = 'vertex.hostToken'

/** The per-launch token the window passed in `#token=…`, kept for reloads, or null outside the desktop app. */
const token: string | null = readToken()

function readToken(): string | null {
    // The window opens `#token=…`, or a deep link such as `#/history?token=…`.
    const hash = window.location.hash.slice(1)
    const queryStart = hash.indexOf('?')
    const route = hash.startsWith('/') ? (queryStart >= 0 ? hash.slice(0, queryStart) : hash) : ''
    const params = new URLSearchParams(hash.startsWith('/') ? (queryStart >= 0 ? hash.slice(queryStart + 1) : '') : hash)
    const fromHash = params.get('token')
    if (fromHash) {
        try {
            sessionStorage.setItem(TOKEN_KEY, fromHash)
        } catch {
            // Storage can be unavailable; the token still works until the page reloads.
        }
        // Keep the secret out of the address the page shows and shares, but keep the page it links to.
        params.delete('token')
        const rest = params.toString()
        const newHash = route ? `#${route}${rest ? `?${rest}` : ''}` : ''
        history.replaceState(history.state, '', window.location.pathname + window.location.search + newHash)
        return fromHash
    }

    try {
        return sessionStorage.getItem(TOKEN_KEY)
    } catch {
        return null
    }
}

/** True inside the Vertex desktop app. The same build also runs in the browser from Vertex.Server, where it's false. */
export function hasDesktopHost(): boolean {
    return token !== null
}

/** A failed host call. `message` is meant for the user and names the fixing command where there is one. */
export class HostError extends Error {
    readonly status: number

    constructor(message: string, status: number) {
        super(message)
        this.name = 'HostError'
        this.status = status
    }
}

/** The raw openapi-fetch client. Prefer `call(host.GET(...))`, which returns the data or throws a HostError. */
export const host = createClient<paths>({baseUrl: ''})

host.use({
    onRequest({request}) {
        if (token === null) {
            throw new HostError('This needs the Vertex desktop app.', 0)
        }
        request.headers.set(TOKEN_HEADER, token)
        return request
    },
})

type FetchResult<T> = {data?: T; error?: unknown; response: Response}

/**
 * Awaits a host call and returns its data, or throws a HostError with the server's ProblemDetails `detail`.
 *
 *     const session = await call(host.GET('/api/session', {signal}))
 */
export async function call<T>(request: Promise<FetchResult<T>>): Promise<T> {
    let result: FetchResult<T>
    try {
        result = await request
    } catch (error) {
        if (error instanceof HostError || (error instanceof DOMException && error.name === 'AbortError')) throw error
        throw new HostError('The Vertex desktop app is not responding. Restart it and try again.', 0)
    }

    if (!result.response.ok) {
        const problem = result.error as {title?: string; detail?: string} | undefined
        throw new HostError(problem?.detail ?? problem?.title ?? `The desktop app returned ${result.response.status}.`, result.response.status)
    }
    return result.data as T
}

/** One event from `GET /api/events`. `type` matches HostEvent.Type in C#, e.g. `workspace.lockConflict`. */
export type HostEvent = {type: string; data: unknown}

const HEARTBEAT_TIMEOUT_MS = 45_000

/**
 * Calls `onEvent` for every host event until `signal` aborts. Reconnects on its own (1 s back-off, up to 30 s) and
 * treats 45 s without the host's 15 s heartbeat as a dead connection. Uses fetch instead of EventSource because
 * EventSource can't send the token header.
 */
export function subscribeHostEvents(onEvent: (event: HostEvent) => void, signal: AbortSignal): void {
    if (token === null) return
    void (async () => {
        let delay = 1_000
        while (!signal.aborted) {
            try {
                await readEventStream(token, onEvent, signal, () => {
                    delay = 1_000
                })
            } catch {
                // Reconnect below.
            }
            if (signal.aborted) return
            await new Promise(resolve => setTimeout(resolve, delay))
            delay = Math.min(delay * 2, 30_000)
        }
    })()
}

async function readEventStream(token: string, onEvent: (event: HostEvent) => void, signal: AbortSignal, onConnected: () => void) {
    const connection = new AbortController()
    const abort = () => connection.abort()
    signal.addEventListener('abort', abort)
    let watchdog = setTimeout(abort, HEARTBEAT_TIMEOUT_MS)

    try {
        const response = await fetch('/api/events', {headers: {[TOKEN_HEADER]: token}, signal: connection.signal})
        if (!response.ok || !response.body) throw new Error(`Event stream returned ${response.status}`)
        onConnected()

        const reader = response.body.pipeThrough(new TextDecoderStream()).getReader()
        let buffer = ''
        for (;;) {
            const {value, done} = await reader.read()
            if (done) return
            clearTimeout(watchdog)
            watchdog = setTimeout(abort, HEARTBEAT_TIMEOUT_MS)

            buffer += value.replace(/\r\n/g, '\n')
            let end: number
            while ((end = buffer.indexOf('\n\n')) >= 0) {
                const block = buffer.slice(0, end)
                buffer = buffer.slice(end + 2)
                const event = parseEvent(block)
                if (event) onEvent(event)
            }
        }
    } finally {
        clearTimeout(watchdog)
        signal.removeEventListener('abort', abort)
    }
}

function parseEvent(block: string): HostEvent | null {
    let type = 'message'
    const data: string[] = []
    for (const line of block.split('\n')) {
        if (line.startsWith(':')) continue
        if (line.startsWith('event:')) type = line.slice(6).trim()
        else if (line.startsWith('data:')) data.push(line.slice(5).trimStart())
    }
    if (data.length === 0) return null
    try {
        return {type, data: JSON.parse(data.join('\n'))}
    } catch {
        return null
    }
}
