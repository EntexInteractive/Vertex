import {RouterProvider} from 'react-router'
import {router} from './routes'
import {SessionProvider} from './SessionProvider'
import {WorkspaceProvider} from './WorkspaceProvider'

export default function App() {
    return (
        <SessionProvider>
            <WorkspaceProvider>
                <RouterProvider router={router}/>
            </WorkspaceProvider>
        </SessionProvider>
    )
}
