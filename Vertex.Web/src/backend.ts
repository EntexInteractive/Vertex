type PhotinoExternal = External & {
    sendMessage?: (message: string) => void
    receiveMessage?: (handler: (message: string) => void) => void
}

type BackendMethods = {
    'app.getGreeting': {payload: {name: string}; result: string}
}

type BackendResponse = {
    type: 'response'
    id: string
    success: boolean
    result: unknown
    error: string | null
}

const pending = new Map<string, (response: BackendResponse) => void>()
let nextId = 0
let listening = false

export function hasPhotinoBackend() {
    const bridge = window.external as PhotinoExternal
    return typeof bridge?.sendMessage === 'function' && typeof bridge.receiveMessage === 'function'
}

export async function callBackend<Method extends keyof BackendMethods>(
    method: Method,
    payload: BackendMethods[Method]['payload'],
): Promise<BackendMethods[Method]['result']> {
    const bridge = window.external as PhotinoExternal
    if (typeof bridge?.sendMessage !== 'function' || typeof bridge.receiveMessage !== 'function') {
        throw new Error('The Photino backend is available in the Vertex desktop app.')
    }

    if (!listening) {
        bridge.receiveMessage(message => {
            let response: BackendResponse | null
            try {
                response = JSON.parse(message)
            } catch {
                return
            }

            if (response?.type === 'response') {
                pending.get(response.id)?.(response)
            }
        })
        listening = true
    }

    const id = String(++nextId)
    const request = JSON.stringify({type: 'request', id, method, payload})
    return new Promise((resolve, reject) => {
        const timeout = window.setTimeout(() => {
            pending.delete(id)
            reject(new Error(`The backend did not respond to ${method}.`))
        }, 10_000)

        pending.set(id, response => {
            window.clearTimeout(timeout)
            pending.delete(id)
            if (response.success) {
                resolve(response.result as BackendMethods[Method]['result'])
            } else {
                reject(new Error(response.error ?? 'The backend request failed.'))
            }
        })

        try {
            bridge.sendMessage!(request)
        } catch (error) {
            window.clearTimeout(timeout)
            pending.delete(id)
            reject(error)
        }
    })
}
