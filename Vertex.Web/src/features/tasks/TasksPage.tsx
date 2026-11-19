import {Avatar} from '../../components/Avatar'
import {Badge, Summary, TableHead} from '../../components/DataTable'
import {Icon} from '../../components/Icon'
import {ViewFrame} from '../../components/ViewFrame'
import {tasks} from '../../mocks/sampleData'

export function TasksPage() {
    return (
        <ViewFrame
            eyebrow="PROJECT"
            title="Tasks"
            description="Work linked to this project and its active branches."
            action={{label: 'New task', icon: 'tasks', disabled: true}}
            preview
        >
            <div className="summary-row">
                <Summary value="8" label="Open"/>
                <Summary value="3" label="In review"/>
                <Summary value="2" label="Due this week" warning/>
                <Summary value="72%" label="Sprint complete"/>
            </div>
            <TableHead cols={['ID', 'TASK', 'STATUS', 'ASSIGNEE', 'DUE']}/>
            <div className="data-table">
                {tasks.map(task => (
                    <div className="data-row task-data" key={task.id}>
                        <code>{task.id}</code>
                        <span>
                            <span className="task-check"/>
                            {task.title}
                        </span>
                        <Badge text={task.status}/>
                        <span>
                            <Avatar person={task.assignee}/>
                            {task.assignee.name}
                        </span>
                        <small>
                            <Icon name="clock" size={13}/>
                            {task.due}
                        </small>
                    </div>
                ))}
            </div>
        </ViewFrame>
    )
}
