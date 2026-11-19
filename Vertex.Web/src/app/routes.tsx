import {createHashRouter, Navigate} from 'react-router'
import {AppLayout} from './AppLayout'

// Hash routes (#/history) work in both hosts without server-side fallbacks: the desktop app's local server and
// Vertex.Server, which serves the same build from its wwwroot.
export const router = createHashRouter([
    {
        path: '/account',
        lazy: async () => ({Component: (await import('../features/auth/AccountPage')).AccountPage}),
    },
    {
        path: '/',
        Component: AppLayout,
        children: [
            {index: true, element: <Navigate to="/workspace" replace/>},
            {path: 'workspace', lazy: async () => ({Component: (await import('../features/workspace/WorkspacePage')).WorkspacePage})},
            {path: 'history', lazy: async () => ({Component: (await import('../features/history/HistoryPage')).HistoryPage})},
            {path: 'tasks', lazy: async () => ({Component: (await import('../features/tasks/TasksPage')).TasksPage})},
            {path: 'bugs', lazy: async () => ({Component: (await import('../features/bugs/BugsPage')).BugsPage})},
            {path: 'graph', lazy: async () => ({Component: (await import('../features/graph/GraphPage')).GraphPage})},
            {path: '*', element: <Navigate to="/workspace" replace/>},
        ],
    },
])
