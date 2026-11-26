import {createContext, useContext} from 'react'
import type {HostError, HostSchemas} from '../host/client'

export type Workspace = HostSchemas['WorkspaceDto']
export type RecentWorkspace = HostSchemas['RecentWorkspaceDto']
export type WorkspaceStatus = HostSchemas['StatusDto']
export type Change = HostSchemas['ChangeDto']
export type Operation = HostSchemas['OperationDto']
export type OperationPhase = HostSchemas['PhaseDto']

/** How an operation ended (`OperationResultDto` in C#). */
export type OperationResult = {
    ok: boolean
    summary: string
    warnings: string[]
    conflicts: string[]
    revision: number | null
}

/** `operation.done`. */
export type OperationDone = {id: string; kind: string; result: OperationResult}

/** Something that happened in the project, from the workspace monitor (`workspace.activity`). */
export type Activity = {
    id: number
    kind: 'Locked' | 'Unlocked' | 'Submitted' | 'Conflict'
    username: string
    host: string
    sameUser: boolean
    paths: string[]
    pathCount: number
    revision: number
    message: string | null
    files: number
    at: number
}

export type WorkspaceState = {
    /** False until the first answer from the host. */
    ready: boolean
    current: Workspace | undefined
    recent: RecentWorkspace[]

    /** The last scan of the open workspace. Kept while a new one runs. */
    status: WorkspaceStatus | undefined
    statusError: HostError | undefined
    scanning: boolean

    /** The running submit, sync, revert, checkout or clone. */
    operation: Operation | undefined
    /** The last operation's result until it's dismissed. */
    result: OperationDone | undefined
    dismissResult: () => void

    /** What others did, newest first. */
    activity: Activity[]
    unreadActivity: number
    markActivityRead: () => void
    /** Why the monitor can't reach the server, or undefined. */
    monitorProblem: string | undefined

    refresh: () => void
    open: (path: string) => Promise<void>
    browse: () => Promise<void>
    forget: (root: string) => Promise<void>
    clone: (projectId: string, folder: string, branch: string | null) => Promise<void>
    init: (folder: string, name: string | null, template: string | null) => Promise<void>
    submit: (message: string, paths: string[]) => Promise<boolean>
    sync: () => Promise<void>
    revert: (paths: string[]) => Promise<void>
    checkout: (branchId: string) => Promise<void>
    cancel: () => Promise<void>
    reveal: (path: string) => Promise<void>
    /** Shows a failure from an action as a result banner. */
    fail: (message: string) => void
}

export const WorkspaceContext = createContext<WorkspaceState | null>(null)

/** The open workspace, its changes and the running operation. */
export function useWorkspace(): WorkspaceState {
    const state = useContext(WorkspaceContext)
    if (!state) throw new Error('useWorkspace must be used inside WorkspaceProvider.')
    return state
}
