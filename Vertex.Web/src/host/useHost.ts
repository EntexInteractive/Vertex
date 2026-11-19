import {type DependencyList, useEffect, useRef, useState} from 'react'
import {hasDesktopHost, HostError, type HostEvent, subscribeHostEvents} from './client'

export type HostQuery<T> = {data: T | undefined; error: HostError | undefined; loading: boolean}

/**
 * Runs a host call when the component mounts or `deps` change, and aborts it on unmount. `loading` is true until the
 * first result; later runs keep showing the previous data until theirs arrives. Outside the desktop app it stays idle
 * (`loading` false, no data), so shared components can use it unconditionally.
 *
 *     const session = useHostQuery(signal => call(host.GET('/api/session', {signal})), [])
 */
export function useHostQuery<T>(query: (signal: AbortSignal) => Promise<T>, deps: DependencyList): HostQuery<T> {
    const [state, setState] = useState<HostQuery<T>>({data: undefined, error: undefined, loading: hasDesktopHost()})

    useEffect(() => {
        if (!hasDesktopHost()) return
        const controller = new AbortController()
        query(controller.signal).then(
            data => setState({data, error: undefined, loading: false}),
            (error: unknown) => {
                if (controller.signal.aborted) return
                const hostError = error instanceof HostError ? error : new HostError(String(error), 0)
                setState(previous => ({data: previous.data, error: hostError, loading: false}))
            },
        )
        return () => controller.abort()
        // The caller owns the dependency list, like useEffect.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, deps)

    return state
}

/** Calls `onEvent` for host events while the component is mounted. */
export function useHostEvents(onEvent: (event: HostEvent) => void): void {
    // Subscribe once and call the latest handler, so a new handler every render doesn't reconnect the stream.
    const handler = useRef(onEvent)
    useEffect(() => {
        handler.current = onEvent
    })

    useEffect(() => {
        const controller = new AbortController()
        subscribeHostEvents(event => handler.current(event), controller.signal)
        return () => controller.abort()
    }, [])
}
