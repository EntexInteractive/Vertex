import {type ReactNode, useCallback, useEffect, useMemo, useRef, useState} from 'react'
import {call, hasDesktopHost, host, HostError} from '../host/client'
import {useHostEvents} from '../host/useHost'
import {useSession} from './session'
import {
    type Activity,
    type Operation,
    type OperationDone,
    type RecentWorkspace,
    type Workspace,
    WorkspaceContext,
    type WorkspaceState,
    type WorkspaceStatus,
} from './workspace'

const MAX_ACTIVITY = 200

function toHostError(error: unknown): HostError {
    return error instanceof HostError ? error : new HostError(String(error), 0)
}

/**
 * Holds the open workspace and keeps it current from host events only (no polling): `workspace.changed` rescans,
 * `operation.progress` / `operation.done` follow the running operation, `workspace.activity` fills the activity feed.
 */
export function WorkspaceProvider({children}: {children: ReactNode}) {
    const {session, selectProject, selectBranch} = useSession()
    const [ready, setReady] = useState(!hasDesktopHost())
    const [current, setCurrent] = useState<Workspace>()
    const [recent, setRecent] = useState<RecentWorkspace[]>([])
    const [status, setStatus] = useState<WorkspaceStatus>()
    const [statusError, setStatusError] = useState<HostError>()
    const [scanning, setScanning] = useState(false)
    const [operation, setOperation] = useState<Operation>()
    const [result, setResult] = useState<OperationDone>()
    const [activity, setActivity] = useState<Activity[]>([])
    const [unreadActivity, setUnreadActivity] = useState(0)
    const [monitorProblem, setMonitorProblem] = useState<string>()

    const operationRef = useRef<Operation | undefined>(undefined)
    const scan = useRef({running: false, again: false})
    const activityId = useRef(0)
    const loggedIn = session?.loggedIn === true

    const loadWorkspaces = useCallback(async () => {
        if (!hasDesktopHost()) return
        try {
            const list = await call(host.GET('/api/workspaces'))
            setCurrent(list.current ?? undefined)
            setRecent(list.recent)
            if (!list.current) setStatus(undefined)
        } catch (error) {
            setStatusError(toHostError(error))
        } finally {
            setReady(true)
        }
    }, [])

    const loadStatus = useCallback(async () => {
        if (!hasDesktopHost()) return
        // One scan at a time; a request during one runs once more afterwards.
        if (scan.current.running) {
            scan.current.again = true
            return
        }
        scan.current.running = true
        setScanning(true)
        try {
            do {
                scan.current.again = false
                try {
                    const next = await call(host.GET('/api/workspaces/current/status'))
                    setStatus(next)
                    setStatusError(undefined)
                } catch (error) {
                    const hostError = toHostError(error)
                    // 409 means no workspace is open: not an error to show here.
                    if (hostError.status === 409) setStatus(undefined)
                    else setStatusError(hostError)
                }
            } while (scan.current.again)
        } finally {
            scan.current.running = false
            setScanning(false)
        }
    }, [])

    useEffect(() => {
        if (!hasDesktopHost()) return
        const timer = setTimeout(() => void loadWorkspaces(), 0)
        call(host.GET('/api/operations/current')).then(op => {
            const running = (op as Operation | undefined) ?? undefined
            operationRef.current = running
            setOperation(running)
        }, () => undefined)
        return () => clearTimeout(timer)
    }, [loadWorkspaces])

    // Scan when a workspace opens, and again after logging in (the lock and revision parts need the server).
    const currentRoot = current?.root
    useEffect(() => {
        if (!currentRoot) return
        const timer = setTimeout(() => void loadStatus(), 0)
        return () => clearTimeout(timer)
    }, [currentRoot, loggedIn, loadStatus])

    // History and the branch list follow the open workspace.
    const projectId = current?.projectId
    const branchId = current?.branchId
    useEffect(() => {
        if (!projectId || !branchId) return
        selectProject(projectId)
        selectBranch(branchId)
        // selectProject/selectBranch change identity with the session state; only the workspace matters here.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [projectId, branchId])

    const addActivity = useCallback((item: Omit<Activity, 'id'>) => {
        activityId.current += 1
        const entry: Activity = {...item, id: activityId.current}
        setActivity(list => [entry, ...list].slice(0, MAX_ACTIVITY))
        setUnreadActivity(n => n + 1)
    }, [])

    useHostEvents(event => {
        switch (event.type) {
            case 'workspace.opened':
                void loadWorkspaces()
                break
            case 'workspace.changed':
                if (!operationRef.current) void loadStatus()
                break
            case 'operation.progress': {
                const op = event.data as Operation
                operationRef.current = op
                setOperation(op)
                break
            }
            case 'operation.done': {
                const done = event.data as OperationDone
                operationRef.current = undefined
                setOperation(undefined)
                setResult(done)
                // A checkout or clone changes the workspace itself.
                if (done.kind === 'checkout' || done.kind === 'clone') void loadWorkspaces()
                void loadStatus()
                break
            }
            case 'workspace.activity': {
                const item = event.data as Omit<Activity, 'id'>
                addActivity(item)
                break
            }
            case 'workspace.lockConflict': {
                const held = event.data as {path: string; username: string; host: string; sameUser: boolean}
                addActivity({kind: 'Conflict', username: held.username, host: held.host, sameUser: held.sameUser, paths: [held.path], pathCount: 1, revision: 0, message: null, files: 0, at: Date.now()})
                break
            }
            case 'workspace.problem':
                setMonitorProblem((event.data as {message: string | null} | null)?.message ?? undefined)
                break
        }
    })

    const start = useCallback(async (request: Promise<Operation>): Promise<boolean> => {
        try {
            const op = await request
            operationRef.current = op
            setOperation(op)
            setResult(undefined)
            return true
        } catch (error) {
            setResult({id: '', kind: 'error', result: {ok: false, summary: toHostError(error).message, warnings: [], conflicts: [], revision: null}})
            return false
        }
    }, [])

    const fail = useCallback((message: string) => {
        setResult({id: '', kind: 'error', result: {ok: false, summary: message, warnings: [], conflicts: [], revision: null}})
    }, [])

    const state = useMemo<WorkspaceState>(() => ({
        ready,
        current,
        recent,
        status: current ? status : undefined,
        statusError,
        scanning,
        operation,
        result,
        dismissResult: () => setResult(undefined),
        activity,
        unreadActivity,
        markActivityRead: () => setUnreadActivity(0),
        monitorProblem,
        refresh: () => {
            void loadWorkspaces()
            void loadStatus()
        },
        open: async path => {
            const opened = await call(host.POST('/api/workspaces/open', {body: {path}}))
            setCurrent(opened)
            setStatus(undefined)
            void loadWorkspaces()
        },
        browse: async () => {
            const picked = await call(host.POST('/api/workspaces/pick-folder', {body: {title: 'Open a Vertex workspace', startIn: current?.root ?? null}}))
            if (!picked.path) return
            const opened = await call(host.POST('/api/workspaces/open', {body: {path: picked.path}}))
            setCurrent(opened)
            setStatus(undefined)
            void loadWorkspaces()
        },
        forget: async root => {
            await call(host.POST('/api/workspaces/forget', {body: {path: root}}))
            void loadWorkspaces()
        },
        clone: async (projectId, folder, branch) => {
            await start(call(host.POST('/api/workspaces/clone', {body: {projectId, folder, branch}})))
            void loadWorkspaces()
        },
        init: async (folder, name, template) => {
            const opened = await call(host.POST('/api/workspaces/init', {body: {folder, name, template}}))
            setCurrent(opened)
            setStatus(undefined)
            void loadWorkspaces()
        },
        submit: (message, paths) => start(call(host.POST('/api/operations/submit', {body: {message, paths, all: false}}))),
        sync: async () => {
            await start(call(host.POST('/api/operations/sync', {body: {revision: null}})))
        },
        revert: async paths => {
            await start(call(host.POST('/api/operations/revert', {body: {paths}})))
        },
        checkout: async id => {
            await start(call(host.POST('/api/operations/checkout', {body: {branchId: id}})))
        },
        cancel: async () => {
            const running = operationRef.current
            if (running) await host.POST('/api/operations/{id}/cancel', {params: {path: {id: running.id}}})
        },
        reveal: async path => {
            try {
                await call(host.POST('/api/workspaces/current/reveal', {body: {path}}))
            } catch (error) {
                fail(toHostError(error).message)
            }
        },
        fail,
    }), [ready, current, recent, status, statusError, scanning, operation, result, activity, unreadActivity, monitorProblem, loadWorkspaces, loadStatus, start, fail])

    return <WorkspaceContext.Provider value={state}>{children}</WorkspaceContext.Provider>
}
